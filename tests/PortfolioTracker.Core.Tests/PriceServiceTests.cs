using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Core.Tests;

public class PriceServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-price-{Guid.NewGuid():N}.db");
    private readonly PriceCacheRepository _cache;

    public PriceServiceTests() => _cache = new PriceCacheRepository(new Database(_dbPath));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private sealed class FakeProvider(Func<string, PriceQuote?> respond) : IPriceProvider
    {
        public Task<PriceQuote?> GetQuoteAsync(string ticker, CancellationToken ct) => Task.FromResult(respond(ticker));
    }

    [Fact]
    public async Task Caches_quotes_and_collects_unresolved()
    {
        var now = DateTimeOffset.UtcNow;
        var svc = new PriceService(new FakeProvider(t => t == "BAD" ? null : new PriceQuote(t, 100m, now)), _cache, TimeSpan.Zero);
        var r = await svc.RefreshAsync(new[] { "MU", "BAD", "DRAM" }, CancellationToken.None);
        Assert.Equal(2, r.Quotes.Count);
        Assert.Equal(new[] { "BAD" }, r.Unresolved);
        Assert.Null(r.Error);
        Assert.Equal(100m, _cache.GetAll()["MU"].Price);
        Assert.False(_cache.GetAll().ContainsKey("BAD"));
    }

    [Fact]
    public async Task Provider_failure_aborts_with_single_error_but_keeps_partial_results()
    {
        var now = DateTimeOffset.UtcNow;
        var svc = new PriceService(new FakeProvider(t =>
            t == "MU" ? new PriceQuote(t, 100m, now) : throw new PriceFetchException("offline")), _cache, TimeSpan.Zero);
        var r = await svc.RefreshAsync(new[] { "MU", "DRAM", "NVDA" }, CancellationToken.None);
        Assert.Single(r.Quotes);
        Assert.Equal("offline", r.Error);
        Assert.True(_cache.GetAll().ContainsKey("MU")); // partial result cached
    }

    [Fact]
    public async Task Progress_reports_each_wave_and_caches_it_before_the_next()
    {
        var now = DateTimeOffset.UtcNow;
        var svc = new PriceService(new FakeProvider(t => new PriceQuote(t, 100m, now)),
            _cache, TimeSpan.Zero, waveSize: 2);
        var waves = new List<List<string>>();
        var r = await svc.RefreshAsync(new[] { "A", "B", "C", "D", "E" }, CancellationToken.None,
            batch =>
            {
                waves.Add(batch.Select(q => q.Ticker).ToList());
                foreach (var q in batch) Assert.True(_cache.GetAll().ContainsKey(q.Ticker));
                return Task.CompletedTask;
            });
        Assert.Equal(3, waves.Count);
        Assert.Equal(new[] { "A", "B" }, waves[0]);
        Assert.Equal(new[] { "E" }, waves[2]);
        Assert.Equal(5, r.Quotes.Count);
    }

    [Fact]
    public async Task Failure_in_a_wave_stops_later_waves_but_keeps_that_waves_successes()
    {
        var now = DateTimeOffset.UtcNow;
        var svc = new PriceService(new FakeProvider(t =>
            t == "BOOM" ? throw new PriceFetchException("offline") : new PriceQuote(t, 100m, now)),
            _cache, TimeSpan.Zero, waveSize: 2);
        var r = await svc.RefreshAsync(new[] { "A", "BOOM", "C", "D" }, CancellationToken.None);
        Assert.Equal("offline", r.Error);
        Assert.Single(r.Quotes);                         // A survived its wave
        Assert.False(r.Quotes.ContainsKey("C"));         // later wave never launched
    }

    private sealed class ConcurrencyProbeProvider : IPriceProvider
    {
        private readonly object _lock = new();
        private int _active;
        public int MaxActive { get; private set; }

        public async Task<PriceQuote?> GetQuoteAsync(string ticker, CancellationToken ct)
        {
            lock (_lock) MaxActive = Math.Max(MaxActive, ++_active);
            await Task.Delay(50, ct);
            lock (_lock) _active--;
            return new PriceQuote(ticker, 10m, DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task Tickers_within_a_wave_fetch_in_parallel()
    {
        var provider = new ConcurrencyProbeProvider();
        var svc = new PriceService(provider, _cache, TimeSpan.Zero, waveSize: 5);
        await svc.RefreshAsync(new[] { "A", "B", "C", "D", "E" }, CancellationToken.None);
        Assert.True(provider.MaxActive > 1, $"expected parallel fetches, saw max {provider.MaxActive}");
    }
}
