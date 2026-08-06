using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Core.Tests;

public class QuoteFreshnessTests
{
    // Wednesday 2026-07-29 14:00 ET (18:00 UTC) — regular session
    private static readonly DateTimeOffset MarketOpen = new(2026, 7, 29, 18, 0, 0, TimeSpan.Zero);
    // Saturday 2026-07-25 14:00 ET
    private static readonly DateTimeOffset Weekend = new(2026, 7, 25, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Frozen_feed_during_market_hours_is_stale()
    {
        var fetched = MarketOpen;
        var observed = MarketOpen.AddMinutes(-20);   // the 13:13-style stuck print
        Assert.True(QuoteFreshness.IsStale(fetched, observed, MarketOpen));
    }

    [Fact]
    public void Healthy_feed_is_not_stale()
    {
        Assert.False(QuoteFreshness.IsStale(MarketOpen, MarketOpen.AddSeconds(-30), MarketOpen));
    }

    [Fact]
    public void Old_observation_outside_market_hours_is_expected_not_stale()
    {
        // weekend fetch returns Friday's close — hours old, perfectly normal
        Assert.False(QuoteFreshness.IsStale(Weekend, Weekend.AddHours(-40), Weekend));
    }

    [Fact]
    public void Missing_observation_never_flags()
    {
        Assert.False(QuoteFreshness.IsStale(MarketOpen, null, MarketOpen));
        Assert.False(QuoteFreshness.IsStale(null, MarketOpen, MarketOpen));
    }

    [Fact]
    public void Newest_observation_picks_max_and_skips_nulls()
    {
        var older = MarketOpen.AddMinutes(-30);
        var newer = MarketOpen.AddMinutes(-1);
        var quotes = new[]
        {
            new PriceQuote("A", 1m, MarketOpen, ObservedAt: older),
            new PriceQuote("B", 1m, MarketOpen, ObservedAt: newer),
            new PriceQuote("C", 1m, MarketOpen),
        };
        Assert.Equal(newer, QuoteFreshness.NewestObservation(quotes));
        Assert.Null(QuoteFreshness.NewestObservation(new[] { new PriceQuote("C", 1m, MarketOpen) }));
    }
}
