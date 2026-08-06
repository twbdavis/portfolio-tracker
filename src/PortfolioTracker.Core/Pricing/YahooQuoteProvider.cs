using System.Net;
using System.Text.Json;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

/// Live-quote source using the same Yahoo chart API as YahooHistoryProvider —
/// free, no key, and independent of Finnhub's pipeline, which makes it the
/// stale-feed backup (see StaleFeedFallbackPriceProvider). The chart meta
/// carries regularMarketPrice, regularMarketTime (our ObservedAt), and the
/// previous session's close. Same strictness rule as the history provider: an
/// unknown symbol degrades to null, a format change or bot wall throws.
public class YahooQuoteProvider(HttpClient http) : IPriceProvider
{
    public async Task<PriceQuote?> GetQuoteAsync(string ticker, CancellationToken ct)
    {
        var url = "https://query1.finance.yahoo.com/v8/finance/chart/" +
                  $"{Uri.EscapeDataString(YahooHistoryProvider.ToYahooSymbol(ticker))}?interval=1d&range=1d";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Yahoo rejects clients without a browser-ish UA
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PortfolioTracker/1.0");
            using var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;   // unknown symbol
            if (resp.StatusCode == (HttpStatusCode)429)
                throw new PriceFetchException("Yahoo Finance rate limit hit.");
            if (!resp.IsSuccessStatusCode)
                throw new PriceFetchException($"Yahoo Finance returned {(int)resp.StatusCode}.");
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseQuoteJson(ticker, body);
        }
        catch (HttpRequestException ex) { throw new PriceFetchException($"Network error: {ex.Message}", ex); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new PriceFetchException("Yahoo Finance request timed out."); }
    }

    public static PriceQuote? ParseQuoteJson(string ticker, string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new PriceFetchException($"Unreadable Yahoo response: {ex.Message}", ex); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("chart", out var chart))
                throw new PriceFetchException("Unexpected Yahoo response shape (no chart element).");
            if (chart.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                return null;   // "Not Found" and friends: no data for this symbol
            if (!chart.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
                return null;
            if (!results[0].TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
                throw new PriceFetchException("Unexpected Yahoo response shape (no chart meta).");

            if (!meta.TryGetProperty("regularMarketPrice", out var priceEl)
                || priceEl.ValueKind != JsonValueKind.Number || !priceEl.TryGetDecimal(out var price) || price <= 0m)
                return null;   // valid symbol, no tradable price (e.g. delisted)

            DateTimeOffset? observed = null;
            if (meta.TryGetProperty("regularMarketTime", out var timeEl)
                && timeEl.ValueKind == JsonValueKind.Number)
                observed = DateTimeOffset.FromUnixTimeSeconds(timeEl.GetInt64()).ToLocalTime();

            decimal? prevClose = null;
            // "previousClose" when present is the regular-session previous close;
            // "chartPreviousClose" (close before the range) equals it for range=1d
            foreach (var name in new[] { "previousClose", "chartPreviousClose" })
                if (meta.TryGetProperty(name, out var pcEl) && pcEl.ValueKind == JsonValueKind.Number
                    && pcEl.TryGetDecimal(out var pc) && pc > 0m)
                { prevClose = pc; break; }

            return new PriceQuote(ticker.Trim().ToUpperInvariant(), price, DateTimeOffset.Now,
                prevClose, observed, Source: "yahoo");
        }
    }
}
