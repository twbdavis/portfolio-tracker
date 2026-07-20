using System.Globalization;
using System.Net;

namespace PortfolioTracker.Core.Pricing;

/// Free daily-history source: https://stooq.com/q/d/l/?s={symbol}&i=d returns
/// CSV (Date,Open,High,Low,Close,Volume), no API key. US tickers get a ".us"
/// suffix and dots become dashes (BRK.B -> brk-b.us). Coverage is imperfect —
/// unknown tickers return "No data" or an empty body, which maps to an empty
/// list per the IHistoryProvider contract.
///
/// NOT REGISTERED since 2026-07-20: stooq.com now fronts the download URL
/// with a JavaScript proof-of-work challenge that a plain HTTP client cannot
/// pass, so every fetch parsed as "no data". YahooHistoryProvider is the
/// active implementation; this stays as the documented swap point should
/// Stooq drop the wall.
public class StooqHistoryProvider(HttpClient http) : IHistoryProvider
{
    public async Task<List<DailyBar>> GetDailyHistoryAsync(string ticker, DateOnly? from, CancellationToken ct)
    {
        var url = $"https://stooq.com/q/d/l/?s={Uri.EscapeDataString(ToStooqSymbol(ticker))}&i=d";
        if (from is DateOnly f) url += $"&d1={f:yyyyMMdd}&d2={DateOnly.FromDateTime(DateTime.Today):yyyyMMdd}";
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return new();
            if (!resp.IsSuccessStatusCode)
                throw new PriceFetchException($"Stooq returned {(int)resp.StatusCode}.");
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseCsv(body);
        }
        catch (HttpRequestException ex) { throw new PriceFetchException($"Network error: {ex.Message}", ex); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new PriceFetchException("Stooq request timed out."); }
    }

    public static string ToStooqSymbol(string ticker) =>
        ticker.Trim().ToLowerInvariant().Replace('.', '-') + ".us";

    /// Lenient by design: Stooq answers unknown symbols with "No data" (or an
    /// empty/HTML body) rather than an error status, and occasionally emits
    /// rows with missing fields — both must degrade to "no bars", never throw.
    public static List<DailyBar> ParseCsv(string csv)
    {
        var bars = new List<DailyBar>();
        if (string.IsNullOrWhiteSpace(csv)) return bars;
        var lines = csv.Split('\n');
        if (!lines[0].TrimEnd('\r').StartsWith("Date,", StringComparison.OrdinalIgnoreCase))
            return bars;   // "No data", HTML error page, or unexpected format
        for (var i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].TrimEnd('\r').Split(',');
            if (parts.Length < 5) continue;
            if (!DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                continue;
            if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)
                || close <= 0m)
                continue;
            bars.Add(new DailyBar(date, close));
        }
        bars.Sort((a, b) => a.Date.CompareTo(b.Date));
        return bars;
    }
}
