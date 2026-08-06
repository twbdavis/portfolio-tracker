using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Tests;

public class PeerExpansionPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly TradingDay = new(2026, 8, 3);

    private static List<CrawlWorkItem> Plan(
        IReadOnlyCollection<string> universe,
        Dictionary<(string, CrawlKind), DateTimeOffset> stamps,
        IReadOnlyCollection<string>? seeds) =>
        CrawlPlanner.Plan(universe, stamps,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase),
            TradingDay, Now, seeds);

    [Fact]
    public void Peer_items_come_from_seeds_not_universe_membership()
    {
        // the held seed isn't in any universe; it still gets a peers fetch
        var items = Plan(new[] { "NVDA" }, new(), new[] { "HELD" });
        Assert.Contains(items, i => i is { Ticker: "HELD", Kind: CrawlKind.Peers });
        Assert.DoesNotContain(items, i => i is { Ticker: "NVDA", Kind: CrawlKind.Peers });
    }

    [Fact]
    public void Peers_respect_their_seven_day_ttl()
    {
        var stamps = new Dictionary<(string, CrawlKind), DateTimeOffset>
        {
            [("FRESH", CrawlKind.Peers)] = Now.AddDays(-6),
            [("STALE", CrawlKind.Peers)] = Now.AddDays(-8),
        };
        var items = Plan(Array.Empty<string>(), stamps, new[] { "FRESH", "STALE" });
        Assert.DoesNotContain(items, i => i is { Ticker: "FRESH", Kind: CrawlKind.Peers });
        Assert.Contains(items, i => i is { Ticker: "STALE", Kind: CrawlKind.Peers });
    }

    [Fact]
    public void No_seeds_means_no_peer_work_and_old_call_sites_still_plan()
    {
        var items = Plan(new[] { "NVDA" }, new(), null);
        Assert.DoesNotContain(items, i => i.Kind == CrawlKind.Peers);

        var dupes = Plan(Array.Empty<string>(), new(), new[] { "MU", "mu" });
        Assert.Single(dupes, i => i.Kind == CrawlKind.Peers);   // seeds dedupe case-insensitively
    }

    [Fact]
    public void Sector_quadrants_classify_on_breadth_and_momentum()
    {
        Assert.Equal(SectorQuadrant.HealthyRally, SectorHealth.Classify(60m, 1m));
        Assert.Equal(SectorQuadrant.Recovering, SectorHealth.Classify(40m, 1m));
        Assert.Equal(SectorQuadrant.Topping, SectorHealth.Classify(60m, -1m));
        Assert.Equal(SectorQuadrant.Breakdown, SectorHealth.Classify(40m, -1m));
        Assert.Equal(SectorQuadrant.HealthyRally, SectorHealth.Classify(50m, 0m));   // boundaries inclusive
        Assert.Null(SectorHealth.Classify(null, 1m));
        Assert.Null(SectorHealth.Classify(60m, null));
    }
}
