using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Core.Tests;

public class StaleFeedFallbackProviderTests
{
    // Wednesday 2026-07-29 14:00 ET (18:00 UTC) — regular session
    private static readonly DateTimeOffset MarketOpen = new(2026, 7, 29, 18, 0, 0, TimeSpan.Zero);
    // Saturday 2026-07-25 14:00 ET
    private static readonly DateTimeOffset Weekend = new(2026, 7, 25, 18, 0, 0, TimeSpan.Zero);

    private class CountingProvider(Func<string, PriceQuote?> fetch) : IPriceProvider
    {
        public int Calls;
        public Task<PriceQuote?> GetQuoteAsync(string ticker, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(fetch(ticker));
        }
    }

    private static PriceQuote Quote(decimal price, DateTimeOffset fetched, DateTimeOffset? observed,
        string? source = null) => new("NVDA", price, fetched, null, observed, source);

    [Fact]
    public async Task Fresh_primary_never_consults_fallback()
    {
        var primary = new CountingProvider(_ => Quote(193m, MarketOpen, MarketOpen.AddSeconds(-30)));
        var fallback = new CountingProvider(_ => Quote(999m, MarketOpen, MarketOpen, "yahoo"));
        var svc = new StaleFeedFallbackPriceProvider(primary, fallback, () => MarketOpen);
        var q = await svc.GetQuoteAsync("NVDA", CancellationToken.None);
        Assert.Equal(193m, q!.Price);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Stale_primary_fails_over_to_fresher_fallback()
    {
        var primary = new CountingProvider(_ => Quote(193.16m, MarketOpen, MarketOpen.AddMinutes(-20)));
        var fallback = new CountingProvider(_ => Quote(195.67m, MarketOpen, MarketOpen.AddSeconds(-10), "yahoo"));
        var svc = new StaleFeedFallbackPriceProvider(primary, fallback, () => MarketOpen);
        var q = await svc.GetQuoteAsync("NVDA", CancellationToken.None);
        Assert.Equal(195.67m, q!.Price);
        Assert.Equal("yahoo", q.Source);
    }

    [Fact]
    public async Task Fallback_with_older_observation_is_ignored()
    {
        var primary = new CountingProvider(_ => Quote(193.16m, MarketOpen, MarketOpen.AddMinutes(-20)));
        var fallback = new CountingProvider(_ => Quote(1m, MarketOpen, MarketOpen.AddMinutes(-40), "yahoo"));
        var svc = new StaleFeedFallbackPriceProvider(primary, fallback, () => MarketOpen);
        var q = await svc.GetQuoteAsync("NVDA", CancellationToken.None);
        Assert.Equal(193.16m, q!.Price);
    }

    [Fact]
    public async Task Fallback_failure_returns_the_stale_primary_quote()
    {
        var primary = new CountingProvider(_ => Quote(193.16m, MarketOpen, MarketOpen.AddMinutes(-20)));
        var fallback = new ThrowingProvider();
        var svc = new StaleFeedFallbackPriceProvider(primary, fallback, () => MarketOpen);
        var q = await svc.GetQuoteAsync("NVDA", CancellationToken.None);
        Assert.Equal(193.16m, q!.Price);
        Assert.Null(q.Source);
    }

    [Fact]
    public async Task Old_observation_outside_market_hours_stays_on_primary()
    {
        // Friday's close fetched on Saturday: hours old, perfectly normal
        var primary = new CountingProvider(_ => Quote(193.16m, Weekend, Weekend.AddHours(-40)));
        var fallback = new CountingProvider(_ => Quote(999m, Weekend, Weekend, "yahoo"));
        var svc = new StaleFeedFallbackPriceProvider(primary, fallback, () => Weekend);
        var q = await svc.GetQuoteAsync("NVDA", CancellationToken.None);
        Assert.Equal(193.16m, q!.Price);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Unknown_symbol_from_primary_stays_null()
    {
        var primary = new CountingProvider(_ => null);
        var fallback = new CountingProvider(_ => Quote(999m, MarketOpen, MarketOpen, "yahoo"));
        var svc = new StaleFeedFallbackPriceProvider(primary, fallback, () => MarketOpen);
        Assert.Null(await svc.GetQuoteAsync("NOPE", CancellationToken.None));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Primary_hard_failure_still_propagates()
    {
        var svc = new StaleFeedFallbackPriceProvider(new ThrowingProvider(),
            new CountingProvider(_ => Quote(999m, MarketOpen, MarketOpen, "yahoo")), () => MarketOpen);
        await Assert.ThrowsAsync<PriceFetchException>(() => svc.GetQuoteAsync("NVDA", CancellationToken.None));
    }

    private class ThrowingProvider : IPriceProvider
    {
        public Task<PriceQuote?> GetQuoteAsync(string ticker, CancellationToken ct) =>
            throw new PriceFetchException("offline");
    }
}

public class YahooQuoteProviderTests
{
    [Fact]
    public void Parses_chart_meta_into_a_sourced_quote()
    {
        var json = """
            {"chart":{"result":[{"meta":{"currency":"USD","symbol":"NVDA",
              "regularMarketPrice":195.675,"regularMarketTime":1785506990,
              "previousClose":195.07,"chartPreviousClose":195.07}}],"error":null}}
            """;
        var q = YahooQuoteProvider.ParseQuoteJson("nvda", json);
        Assert.NotNull(q);
        Assert.Equal("NVDA", q!.Ticker);
        Assert.Equal(195.675m, q.Price);
        Assert.Equal(195.07m, q.PrevClose);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785506990).ToLocalTime(), q.ObservedAt);
        Assert.Equal("yahoo", q.Source);
    }

    [Fact]
    public void Unknown_symbol_error_object_degrades_to_null()
    {
        var json = """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found"}}}""";
        Assert.Null(YahooQuoteProvider.ParseQuoteJson("NOPE", json));
    }

    [Fact]
    public void Missing_price_degrades_to_null()
    {
        var json = """{"chart":{"result":[{"meta":{"symbol":"GONE"}}],"error":null}}""";
        Assert.Null(YahooQuoteProvider.ParseQuoteJson("GONE", json));
    }

    [Fact]
    public void Garbage_or_shape_change_throws_instead_of_silently_failing()
    {
        Assert.Throws<PriceFetchException>(() => YahooQuoteProvider.ParseQuoteJson("NVDA", "<html>bot wall</html>"));
        Assert.Throws<PriceFetchException>(() => YahooQuoteProvider.ParseQuoteJson("NVDA", """{"nope":true}"""));
    }
}
