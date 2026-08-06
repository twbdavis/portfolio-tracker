using System.Net;
using System.Text.Json;

namespace PortfolioTracker.Core.Pricing;

public interface ICompanyDescriptionProvider
{
    /// Plain-English business summary for a ticker; null when the provider
    /// has nothing for the symbol. PriceFetchException on transport failure.
    Task<string?> GetBusinessSummaryAsync(string ticker, CancellationToken ct);
}

/// Yahoo quoteSummary assetProfile, the only keyless source of a real
/// "what the company does" paragraph. Unlike the chart API it requires the
/// cookie + crumb dance: hit fc.yahoo.com for a session cookie, exchange it
/// for a crumb, then pass the crumb on every data call. The pair is cached
/// and refreshed once on a 401. Owns its HttpClient because the cookie
/// container is integral to the protocol.
public class YahooProfileProvider : ICompanyDescriptionProvider
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PortfolioTracker/1.0";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _crumbLock = new(1, 1);
    private string? _crumb;

    public YahooProfileProvider(TimeSpan? timeout = null)
    {
        _http = new HttpClient(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        })
        { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    public async Task<string?> GetBusinessSummaryAsync(string ticker, CancellationToken ct)
    {
        try
        {
            var body = await GetWithCrumbAsync(ticker, ct).ConfigureAwait(false);
            return body is null ? null : ParseSummary(body);
        }
        catch (HttpRequestException ex)
        { throw new PriceFetchException($"Network error: {ex.Message}", ex); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new PriceFetchException("Yahoo Finance request timed out."); }
    }

    private async Task<string?> GetWithCrumbAsync(string ticker, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var crumb = await EnsureCrumbAsync(refresh: attempt > 0, ct).ConfigureAwait(false);
            var url = "https://query1.finance.yahoo.com/v10/finance/quoteSummary/" +
                      $"{Uri.EscapeDataString(YahooHistoryProvider.ToYahooSymbol(ticker))}" +
                      $"?modules=assetProfile&crumb={Uri.EscapeDataString(crumb)}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) continue;   // stale crumb: refresh once
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;    // unknown symbol
            if (resp.StatusCode == (HttpStatusCode)429)
                throw new PriceFetchException("Yahoo Finance rate limit hit.");
            if (!resp.IsSuccessStatusCode)
                throw new PriceFetchException($"Yahoo Finance returned {(int)resp.StatusCode}.");
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        throw new PriceFetchException("Yahoo Finance rejected the session crumb twice.");
    }

    private async Task<string> EnsureCrumbAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && _crumb is string cached) return cached;
        await _crumbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _crumb is string reread) return reread;
            // fc.yahoo.com answers 404 but plants the session cookie
            try { using var _ = await _http.GetAsync("https://fc.yahoo.com", ct).ConfigureAwait(false); }
            catch (HttpRequestException) { /* the cookie is set even on error paths */ }
            using var resp = await _http.GetAsync(
                "https://query1.finance.yahoo.com/v1/test/getcrumb", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new PriceFetchException($"Yahoo crumb request returned {(int)resp.StatusCode}.");
            var crumb = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (crumb.Length == 0 || crumb.Contains('{'))
                throw new PriceFetchException("Yahoo crumb response was empty or malformed.");
            _crumb = crumb;
            return crumb;
        }
        finally
        {
            _crumbLock.Release();
        }
    }

    public static string? ParseSummary(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        { throw new PriceFetchException($"Unreadable Yahoo response: {ex.Message}", ex); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("quoteSummary", out var qs)
                || !qs.TryGetProperty("result", out var results)
                || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return null;
            if (!results[0].TryGetProperty("assetProfile", out var profile)
                || profile.ValueKind != JsonValueKind.Object)
                return null;
            if (!profile.TryGetProperty("longBusinessSummary", out var summary)
                || summary.ValueKind != JsonValueKind.String)
                return null;
            var text = summary.GetString()?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
