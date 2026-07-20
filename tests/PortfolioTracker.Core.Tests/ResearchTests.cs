using PortfolioTracker.Core;
using PortfolioTracker.Core.Alerts;
using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Core.Tests;

public class HumanNumberTests
{
    [Theory]
    [InlineData("1230000000000", "$1.2T")]
    [InlineData("45300000000", "$45.3B")]
    [InlineData("890000000", "$890M")]
    [InlineData("2500000", "$2.5M")]
    [InlineData("12345", "$12,345")]
    [InlineData("0", "$0")]
    [InlineData("-45300000000", "-$45.3B")]
    public void Money_compacts_large_dollar_amounts(string dollars, string expected) =>
        Assert.Equal(expected, HumanNumber.Money(decimal.Parse(dollars)));

    [Fact]
    public void Money_drops_trailing_zero_decimal() =>
        Assert.Equal("$45B", HumanNumber.Money(45_000_000_000m));
}

public class BuyRatioTests
{
    [Fact]
    public void Buy_ratio_counts_strong_buy_and_buy_over_total()
    {
        var t = new RecommendationTrend { StrongBuy = 13, Buy = 24, Hold = 7, Sell = 5, StrongSell = 1 };
        Assert.Equal(0.74m, Math.Round(t.BuyRatio!.Value, 2));
    }

    [Fact]
    public void Buy_ratio_is_null_without_coverage() =>
        Assert.Null(new RecommendationTrend().BuyRatio);
}

public class WatchlistAlertTests
{
    private static WatchlistItem Item(decimal? target = 25m, bool enabled = true, DateTimeOffset? triggered = null) =>
        new() { Ticker = "GLXY", TargetBuyPrice = target, AlertEnabled = enabled, AlertTriggeredAt = triggered };

    [Fact]
    public void Fires_at_or_below_buy_target()
    {
        Assert.True(AlertEvaluator.ShouldFire(Item(), 24.10m));
        Assert.True(AlertEvaluator.ShouldFire(Item(), 25.00m));
        Assert.False(AlertEvaluator.ShouldFire(Item(), 25.01m));
    }

    [Fact]
    public void One_shot_never_refires_until_rearmed()
    {
        var stamped = Item(triggered: DateTimeOffset.Now);
        Assert.False(AlertEvaluator.ShouldFire(stamped, 20m));
        stamped.AlertTriggeredAt = null;   // re-arm
        Assert.True(AlertEvaluator.ShouldFire(stamped, 20m));
    }

    [Fact]
    public void Disabled_or_targetless_items_never_fire()
    {
        Assert.False(AlertEvaluator.ShouldFire(Item(enabled: false), 20m));
        Assert.False(AlertEvaluator.ShouldFire(Item(target: null), 20m));
    }
}

public class ResearchCacheTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-research-{Guid.NewGuid():N}.db");
    private readonly Database _db;

    public ResearchCacheTests() => _db = new Database(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Get_honors_ttl()
    {
        var cache = new ResearchCacheRepository(_db);
        cache.Put("MU", "profile", "{\"a\":1}", DateTimeOffset.Now.AddHours(-25));
        Assert.Null(cache.Get("MU", "profile", TimeSpan.FromHours(24)));      // stale
        cache.Put("MU", "profile", "{\"a\":2}", DateTimeOffset.Now.AddHours(-2));
        Assert.Equal("{\"a\":2}", cache.Get("MU", "profile", TimeSpan.FromHours(24)));
        Assert.Null(cache.Get("MU", "profile", TimeSpan.FromHours(1)));       // news-style ttl
        Assert.Null(cache.Get("MU", "metrics", TimeSpan.FromHours(24)));      // kind is part of the key
    }

    private sealed class CountingProvider : IResearchDataProvider
    {
        public int ProfileCalls, MetricsCalls;
        public Task<List<SymbolSearchResult>> SearchAsync(string q, CancellationToken ct) => Task.FromResult(new List<SymbolSearchResult>());
        public Task<QuoteDetail?> GetQuoteDetailAsync(string t, CancellationToken ct) => Task.FromResult<QuoteDetail?>(null);
        public Task<CompanyProfile?> GetProfileAsync(string t, CancellationToken ct)
        {
            ProfileCalls++;
            return Task.FromResult<CompanyProfile?>(new CompanyProfile { Ticker = t, Name = "Micron" });
        }
        public Task<FundamentalMetrics?> GetMetricsAsync(string t, CancellationToken ct)
        {
            MetricsCalls++;
            return Task.FromResult<FundamentalMetrics?>(null);   // ETF-style: nothing available
        }
        public Task<List<RecommendationTrend>> GetRecommendationsAsync(string t, CancellationToken ct) => Task.FromResult(new List<RecommendationTrend>());
        public Task<List<EarningsQuarter>> GetEarningsAsync(string t, CancellationToken ct) => Task.FromResult(new List<EarningsQuarter>());
        public Task<DateOnly?> GetNextEarningsDateAsync(string t, CancellationToken ct) => Task.FromResult<DateOnly?>(null);
        public Task<List<NewsItem>> GetCompanyNewsAsync(string t, DateOnly f, DateOnly to, CancellationToken ct) => Task.FromResult(new List<NewsItem>());
    }

    [Fact]
    public async Task Service_serves_repeat_lookups_from_cache()
    {
        var provider = new CountingProvider();
        var svc = new ResearchService(provider, new ResearchCacheRepository(_db), new RateGate(TimeSpan.Zero));

        var first = await svc.GetProfileAsync("MU", CancellationToken.None);
        var second = await svc.GetProfileAsync("mu", CancellationToken.None);  // case-insensitive key
        Assert.Equal("Micron", first!.Name);
        Assert.Equal("Micron", second!.Name);
        Assert.Equal(1, provider.ProfileCalls);
    }

    [Fact]
    public async Task Service_caches_null_results_so_etfs_do_not_refetch()
    {
        var provider = new CountingProvider();
        var svc = new ResearchService(provider, new ResearchCacheRepository(_db), new RateGate(TimeSpan.Zero));

        Assert.Null(await svc.GetMetricsAsync("BSV", CancellationToken.None));
        Assert.Null(await svc.GetMetricsAsync("BSV", CancellationToken.None));
        Assert.Equal(1, provider.MetricsCalls);
    }
}
