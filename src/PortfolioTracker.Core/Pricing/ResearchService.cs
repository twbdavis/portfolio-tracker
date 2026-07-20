using System.Text.Json;
using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

/// Cached, rate-limited access to research data. Slow-moving data (profile,
/// fundamentals, recommendations, earnings) caches 24h; news caches 1h. Null
/// provider results are cached too, so an ETF's missing fundamentals don't
/// re-fire the endpoint on every open.
public class ResearchService(IResearchDataProvider provider, ResearchCacheRepository cache, RateGate gate)
{
    private static readonly TimeSpan SlowTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NewsTtl = TimeSpan.FromHours(1);
    public const int NewsDays = 30;

    public async Task<List<SymbolSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return await provider.SearchAsync(query, ct).ConfigureAwait(false);
    }

    /// Quotes are live (short-lived by nature) — gated but never cached here.
    public async Task<QuoteDetail?> GetQuoteDetailAsync(string ticker, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return await provider.GetQuoteDetailAsync(ticker, ct).ConfigureAwait(false);
    }

    public Task<CompanyProfile?> GetProfileAsync(string ticker, CancellationToken ct) =>
        CachedAsync<CompanyProfile?>(ticker, "profile", SlowTtl, c => provider.GetProfileAsync(ticker, c), ct);

    public Task<FundamentalMetrics?> GetMetricsAsync(string ticker, CancellationToken ct) =>
        CachedAsync<FundamentalMetrics?>(ticker, "metrics", SlowTtl, c => provider.GetMetricsAsync(ticker, c), ct);

    public Task<List<RecommendationTrend>> GetRecommendationsAsync(string ticker, CancellationToken ct) =>
        CachedAsync(ticker, "recommendations", SlowTtl, c => provider.GetRecommendationsAsync(ticker, c), ct)!;

    public Task<List<EarningsQuarter>> GetEarningsAsync(string ticker, CancellationToken ct) =>
        CachedAsync(ticker, "earnings", SlowTtl, c => provider.GetEarningsAsync(ticker, c), ct)!;

    public Task<DateOnly?> GetNextEarningsDateAsync(string ticker, CancellationToken ct) =>
        CachedAsync<DateOnly?>(ticker, "earningsdate", SlowTtl, c => provider.GetNextEarningsDateAsync(ticker, c), ct);

    public Task<List<NewsItem>> GetNewsAsync(string ticker, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return CachedAsync(ticker, "news", NewsTtl,
            c => provider.GetCompanyNewsAsync(ticker, today.AddDays(-NewsDays), today, c), ct)!;
    }

    private async Task<T> CachedAsync<T>(string ticker, string kind, TimeSpan ttl,
        Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        var cached = cache.Get(ticker, kind, ttl);
        if (cached is not null) return JsonSerializer.Deserialize<T>(cached)!;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var value = await fetch(ct).ConfigureAwait(false);
        cache.Put(ticker, kind, JsonSerializer.Serialize(value), DateTimeOffset.Now);
        return value;
    }
}
