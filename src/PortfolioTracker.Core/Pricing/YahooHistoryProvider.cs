using System.Net;
using System.Text.Json;

namespace PortfolioTracker.Core.Pricing;

/// Daily-history source using Yahoo Finance's chart API
/// (query1.finance.yahoo.com/v8/finance/chart) — free, no key, JSON. Replaced
/// Stooq (2026-07-20) after stooq.com put a JavaScript proof-of-work challenge
/// in front of its CSV downloads, which a plain HTTP client cannot (and should
/// not try to) pass. Unofficial endpoint: stable for years, but error
/// classification below is deliberately strict so a format change surfaces as
/// a fetch error, never as silently-empty history.
///
/// Adjusted closes are preferred over raw so splits don't shear the SMA and
/// return series. Class shares map dot->dash (BRK.B -> BRK-B).
public class YahooHistoryProvider(HttpClient http) : IHistoryProvider
{
    public async Task<List<DailyBar>> GetDailyHistoryAsync(string ticker, DateOnly? from, CancellationToken ct)
    {
        var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-730));
        var period1 = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var period2 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // a start at/after now (caller already caught up today) must not become
        // an inverted range — Yahoo answers those with HTTP 400
        if (period1 >= period2) period1 = period2 - 86400;
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(ToYahooSymbol(ticker))}" +
                  $"?period1={period1}&period2={period2}&interval=1d&events=history";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Yahoo rejects clients without a browser-ish UA
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PortfolioTracker/1.0");
            using var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return new();   // unknown symbol
            if (resp.StatusCode == (HttpStatusCode)429)
                throw new PriceFetchException("Yahoo Finance rate limit hit; history will retry next pass.");
            if (!resp.IsSuccessStatusCode)
                throw new PriceFetchException($"Yahoo Finance returned {(int)resp.StatusCode}.");
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseChartJson(body);
        }
        catch (HttpRequestException ex) { throw new PriceFetchException($"Network error: {ex.Message}", ex); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new PriceFetchException("Yahoo Finance request timed out."); }
    }

    public static string ToYahooSymbol(string ticker) =>
        ticker.Trim().ToUpperInvariant().Replace('.', '-');

    /// Strict: an unknown symbol (error object / empty result) degrades to an
    /// empty list, but a non-JSON body or missing structure throws — a bot
    /// wall or format change must never be stamped as "this ticker has no
    /// history".
    public static List<DailyBar> ParseChartJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new PriceFetchException($"Unreadable Yahoo response: {ex.Message}", ex); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("chart", out var chart))
                throw new PriceFetchException("Unexpected Yahoo response shape (no chart element).");
            if (chart.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                return new();   // "Not Found" and friends: no data for this symbol
            if (!chart.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
                return new();

            var result = results[0];
            if (!result.TryGetProperty("timestamp", out var timestamps)
                || timestamps.ValueKind != JsonValueKind.Array)
                return new();   // valid symbol, no bars in range

            // adjusted closes when present, raw closes otherwise
            JsonElement closes = default;
            var haveCloses = false;
            if (result.TryGetProperty("indicators", out var indicators))
            {
                if (indicators.TryGetProperty("adjclose", out var adjArr)
                    && adjArr.ValueKind == JsonValueKind.Array && adjArr.GetArrayLength() > 0
                    && adjArr[0].TryGetProperty("adjclose", out closes)
                    && closes.ValueKind == JsonValueKind.Array)
                    haveCloses = true;
                else if (indicators.TryGetProperty("quote", out var quoteArr)
                    && quoteArr.ValueKind == JsonValueKind.Array && quoteArr.GetArrayLength() > 0
                    && quoteArr[0].TryGetProperty("close", out closes)
                    && closes.ValueKind == JsonValueKind.Array)
                    haveCloses = true;
            }
            if (!haveCloses)
                throw new PriceFetchException("Unexpected Yahoo response shape (no close series).");

            var bars = new List<DailyBar>();
            var count = Math.Min(timestamps.GetArrayLength(), closes.GetArrayLength());
            for (var i = 0; i < count; i++)
            {
                // trading halts / partial rows come through as nulls: skip
                if (closes[i].ValueKind != JsonValueKind.Number) continue;
                if (!closes[i].TryGetDecimal(out var close) || close <= 0m) continue;
                if (timestamps[i].ValueKind != JsonValueKind.Number) continue;
                var ts = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64());
                // US session timestamps (13:30/14:30 UTC) land on the ET date
                bars.Add(new DailyBar(DateOnly.FromDateTime(ts.UtcDateTime.Date), Math.Round(close, 4)));
            }
            bars.Sort((a, b) => a.Date.CompareTo(b.Date));
            // the last bar can be today's in-flight session; keep it — the
            // DailyClose live-snapshot upsert semantics already tolerate that
            return bars;
        }
    }
}
