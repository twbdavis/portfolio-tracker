using System.Net.Http.Json;
using System.Text.Json.Serialization;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

public class FinnhubPriceProvider(HttpClient http, Func<string?> apiKey) : IPriceProvider
{
    private class QuoteDto
    {
        [JsonPropertyName("c")] public decimal Current { get; set; }
        [JsonPropertyName("pc")] public decimal PrevClose { get; set; }
        [JsonPropertyName("t")] public long Timestamp { get; set; }   // unix seconds the price was observed
    }

    public async Task<PriceQuote?> GetQuoteAsync(string ticker, CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new PriceFetchException("Finnhub API key not set. Open Settings to add one.");
        try
        {
            var url = $"https://finnhub.io/api/v1/quote?symbol={Uri.EscapeDataString(ticker)}&token={Uri.EscapeDataString(key)}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new PriceFetchException($"Finnhub returned {(int)resp.StatusCode} for {ticker}.");
            var dto = await resp.Content.ReadFromJsonAsync<QuoteDto>(cancellationToken: ct).ConfigureAwait(false)
                      ?? throw new PriceFetchException($"Empty Finnhub response for {ticker}.");
            if (dto.Current == 0m && dto.PrevClose == 0m) return null; // unknown symbol
            return new PriceQuote(ticker, dto.Current, DateTimeOffset.Now,
                dto.PrevClose > 0m ? dto.PrevClose : null,
                dto.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(dto.Timestamp).ToLocalTime() : null);
        }
        catch (HttpRequestException ex) { throw new PriceFetchException($"Network error: {ex.Message}", ex); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new PriceFetchException("Finnhub request timed out."); }
    }
}
