using PortfolioTracker.Core.Pricing;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Tests;

public class CrawlPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 21, 30, 0, TimeSpan.Zero); // Mon 5:30 PM ET
    private static readonly DateOnly LastSession = new(2026, 7, 20);

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static List<CrawlWorkItem> Plan(
        IReadOnlyCollection<string> tickers,
        Dictionary<(string, CrawlKind), DateTimeOffset>? stamps = null,
        IReadOnlySet<string>? live = null,
        IReadOnlySet<string>? userClassified = null,
        Dictionary<string, DateOnly>? latest = null) =>
        CrawlPlanner.Plan(tickers, stamps ?? new(), live ?? None, userClassified ?? None,
            latest ?? new(), LastSession, Now);

    [Fact]
    public void Unstamped_ticker_gets_all_five_kinds()
    {
        var plan = Plan(new[] { "NVDA" });
        Assert.Equal(5, plan.Count);
        Assert.All(plan, i => Assert.Equal("NVDA", i.Ticker));
        Assert.Equal(Enum.GetValues<CrawlKind>().OrderBy(k => k),
            plan.Select(i => i.Kind).OrderBy(k => k));
    }

    [Fact]
    public void Fresh_stamps_produce_empty_plan()
    {
        var stamps = Enum.GetValues<CrawlKind>()
            .ToDictionary(k => ("NVDA", k), _ => Now.AddHours(-1));
        var plan = Plan(new[] { "NVDA" }, stamps, latest: new() { ["NVDA"] = LastSession });
        Assert.Empty(plan);
    }

    [Fact]
    public void Ttls_expire_per_kind()
    {
        var stamps = new Dictionary<(string, CrawlKind), DateTimeOffset>
        {
            [("NVDA", CrawlKind.Profile)] = Now.AddDays(-8),          // 7d TTL: stale
            [("NVDA", CrawlKind.Metrics)] = Now.AddHours(-25),        // 24h TTL: stale
            [("NVDA", CrawlKind.Recommendations)] = Now.AddHours(-23),// fresh
            [("NVDA", CrawlKind.Quote)] = Now.AddHours(-1),           // fresh
            [("NVDA", CrawlKind.History)] = Now.AddHours(-1),         // fresh
        };
        var plan = Plan(new[] { "NVDA" }, stamps, latest: new() { ["NVDA"] = LastSession });
        Assert.Equal(new[] { CrawlKind.Profile, CrawlKind.Metrics }, plan.Select(i => i.Kind).ToArray());
    }

    [Fact]
    public void Profile_just_under_ttl_is_fresh()
    {
        var stamps = new Dictionary<(string, CrawlKind), DateTimeOffset>
        {
            [("NVDA", CrawlKind.Profile)] = Now.AddDays(-7).AddMinutes(1),
        };
        var plan = Plan(new[] { "NVDA" }, stamps);
        Assert.DoesNotContain(plan, i => i.Kind == CrawlKind.Profile);
    }

    [Fact]
    public void Held_or_watched_tickers_skip_quote_kind()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NVDA" };
        var plan = Plan(new[] { "NVDA", "MU" }, live: live);
        Assert.DoesNotContain(plan, i => i.Ticker == "NVDA" && i.Kind == CrawlKind.Quote);
        Assert.Contains(plan, i => i.Ticker == "MU" && i.Kind == CrawlKind.Quote);
    }

    [Fact]
    public void User_classified_tickers_skip_profile_kind()
    {
        var user = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NVDA" };
        var plan = Plan(new[] { "NVDA" }, userClassified: user);
        Assert.DoesNotContain(plan, i => i.Kind == CrawlKind.Profile);
        Assert.Contains(plan, i => i.Kind == CrawlKind.Metrics);
    }

    [Fact]
    public void History_appends_only_while_lagging_the_last_session()
    {
        // caught up: stamped long ago but series reaches the last session
        var stamps = new Dictionary<(string, CrawlKind), DateTimeOffset>
        {
            [("NVDA", CrawlKind.History)] = Now.AddDays(-3),
            [("MU", CrawlKind.History)] = Now.AddDays(-3),
            [("VOO", CrawlKind.History)] = Now.AddHours(-1),
        };
        var latest = new Dictionary<string, DateOnly>
        {
            ["NVDA"] = LastSession,
            ["MU"] = LastSession.AddDays(-3),   // lagging
            ["VOO"] = LastSession.AddDays(-3),  // lagging but stamp is fresh (just tried)
        };
        var plan = Plan(new[] { "NVDA", "MU", "VOO" }, stamps, latest: latest);
        var history = plan.Where(i => i.Kind == CrawlKind.History).Select(i => i.Ticker).ToList();
        Assert.Equal(new[] { "MU" }, history);
    }

    [Fact]
    public void History_with_no_stamp_backfills_even_when_recently_closed_data_exists()
    {
        var latest = new Dictionary<string, DateOnly> { ["NVDA"] = LastSession };
        var plan = Plan(new[] { "NVDA" }, latest: latest);
        Assert.Contains(plan, i => i.Kind == CrawlKind.History);
    }

    [Fact]
    public void Stooq_coverage_gap_stays_quiet_until_ttl()
    {
        // ticker Stooq doesn't know: stamped after the empty fetch, no closes.
        // It must not re-enqueue until the TTL expires.
        var stamps = new Dictionary<(string, CrawlKind), DateTimeOffset>
        {
            [("ZZZZ", CrawlKind.History)] = Now.AddHours(-2),
        };
        var plan = Plan(new[] { "ZZZZ" }, stamps);
        Assert.DoesNotContain(plan, i => i.Kind == CrawlKind.History);

        stamps[("ZZZZ", CrawlKind.History)] = Now.AddHours(-25);
        plan = Plan(new[] { "ZZZZ" }, stamps);
        Assert.Contains(plan, i => i.Kind == CrawlKind.History);   // retries daily, bounded
    }

    [Fact]
    public void Restart_resume_processed_items_drop_out_of_the_next_plan()
    {
        // simulate an interrupted pass: NVDA fully stamped mid-crawl, MU not yet
        var stamps = Enum.GetValues<CrawlKind>()
            .ToDictionary(k => ("NVDA", k), _ => Now.AddMinutes(-10));
        var plan = Plan(new[] { "NVDA", "MU" }, stamps, latest: new() { ["NVDA"] = LastSession });
        Assert.All(plan, i => Assert.Equal("MU", i.Ticker));
        Assert.Equal(5, plan.Count);
    }

    [Fact]
    public void Duplicate_tickers_across_universes_plan_once()
    {
        var plan = Plan(new[] { "NVDA", "nvda", "NVDA" });
        Assert.Equal(5, plan.Count);
    }

    [Fact]
    public void Plan_is_deterministic_and_sorted_by_ticker()
    {
        var plan = Plan(new[] { "MU", "AMD", "NVDA" });
        var tickers = plan.Select(i => i.Ticker).Distinct().ToList();
        Assert.Equal(new[] { "AMD", "MU", "NVDA" }, tickers);
    }

    [Fact]
    public void HistoryFetchStart_backfills_fully_when_series_is_a_token()
    {
        var today = new DateOnly(2026, 7, 20);
        // a single live-snapshot close (dated today!) must NOT collapse the
        // backfill into a starts-tomorrow request
        var start = CrawlPlanner.HistoryFetchStart(1, today, today);
        Assert.Equal(today.AddDays(-(int)CrawlPlanner.HistoryBackfill.TotalDays), start);

        Assert.Equal(start, CrawlPlanner.HistoryFetchStart(0, null, today));
        Assert.Equal(start, CrawlPlanner.HistoryFetchStart(29, today.AddDays(-40), today));
    }

    [Fact]
    public void HistoryFetchStart_appends_from_next_day_once_established()
    {
        var today = new DateOnly(2026, 7, 20);
        Assert.Equal(new DateOnly(2026, 7, 18),
            CrawlPlanner.HistoryFetchStart(300, new DateOnly(2026, 7, 17), today));
        // caught up through today: clamp to today, never the future
        Assert.Equal(today, CrawlPlanner.HistoryFetchStart(300, today, today));
    }
}

public class InteractiveGateTests
{
    [Fact]
    public void Quiet_when_idle_and_cooldown_elapsed()
    {
        var gate = new InteractiveGate();
        Assert.True(gate.IsQuiet(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Busy_while_a_request_is_in_flight()
    {
        var gate = new InteractiveGate();
        using (gate.Begin())
        {
            Assert.False(gate.IsQuiet(TimeSpan.Zero));
        }
        Assert.True(gate.IsQuiet(TimeSpan.Zero));
    }

    [Fact]
    public void Cooldown_holds_after_the_last_request_ends()
    {
        var gate = new InteractiveGate();
        gate.Begin().Dispose();
        Assert.False(gate.IsQuiet(TimeSpan.FromSeconds(30)));
        Assert.False(gate.IsQuiet(TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow.AddSeconds(29)));
        Assert.True(gate.IsQuiet(TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow.AddSeconds(31)));
    }

    [Fact]
    public void Overlapping_requests_stay_busy_until_all_end()
    {
        var gate = new InteractiveGate();
        var a = gate.Begin();
        var b = gate.Begin();
        a.Dispose();
        Assert.False(gate.IsQuiet(TimeSpan.Zero));
        b.Dispose();
        Assert.True(gate.IsQuiet(TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public void Double_dispose_is_harmless()
    {
        var gate = new InteractiveGate();
        var scope = gate.Begin();
        scope.Dispose();
        scope.Dispose();
        Assert.True(gate.IsQuiet(TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1)));
    }
}
