using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;

namespace PortfolioTracker.Core.Tests;

public class OutcomeMathTests
{
    private static List<DailyCloseRow> Daily(string ticker, DateOnly start, params decimal[] closes)
    {
        // consecutive calendar days, deliberately including weekends being
        // skipped by callers is NOT assumed here — dates are explicit
        return closes.Select((c, i) => new DailyCloseRow
        {
            Ticker = ticker, Date = start.AddDays(i), Close = c,
        }).ToList();
    }

    private static readonly DateOnly Fire = new(2026, 3, 2);

    [Fact]
    public void CloseOnOrBefore_picks_the_last_close_at_or_before_the_date()
    {
        var closes = Daily("A", Fire.AddDays(-2), 98m, 99m, 100m);   // last lands on Fire
        Assert.Equal(100m, OutcomeMath.CloseOnOrBefore(closes, Fire));
        Assert.Equal(99m, OutcomeMath.CloseOnOrBefore(closes, Fire.AddDays(-1)));
        Assert.Null(OutcomeMath.CloseOnOrBefore(closes, Fire.AddDays(-3)));
    }

    [Fact]
    public void ForwardReturn_matures_exactly_when_a_close_reaches_the_target_date()
    {
        // closes through fire+29: not matured
        var short29 = Daily("A", Fire, Enumerable.Repeat(100m, 30).ToArray());
        Assert.Null(OutcomeMath.ForwardReturn(short29, Fire, 100m, 30));

        // a close ON fire+30: matured, uses that close
        var exact = Daily("A", Fire, Enumerable.Range(0, 31).Select(i => 100m + i).ToArray());
        Assert.Equal(30m, OutcomeMath.ForwardReturn(exact, Fire, 100m, 30));
    }

    [Fact]
    public void ForwardReturn_uses_the_first_close_after_a_gap_over_the_target()
    {
        // target = fire+30 falls in a gap; first available close is fire+33
        var closes = Daily("A", Fire, 100m, 101m);
        closes.Add(new DailyCloseRow { Ticker = "A", Date = Fire.AddDays(33), Close = 110m });
        Assert.Equal(10m, OutcomeMath.ForwardReturn(closes, Fire, 100m, 30));
    }

    [Fact]
    public void ForwardReturn_guards_a_zero_baseline()
    {
        var closes = Daily("A", Fire, Enumerable.Repeat(100m, 40).ToArray());
        Assert.Null(OutcomeMath.ForwardReturn(closes, Fire, 0m, 30));
    }

    [Fact]
    public void SectorMedian_needs_three_matured_members()
    {
        var closesByTicker = new Dictionary<string, List<DailyCloseRow>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Daily("A", Fire, Enumerable.Range(0, 31).Select(i => 100m + i).ToArray()),
            ["B"] = Daily("B", Fire, Enumerable.Range(0, 31).Select(i => 100m + 2m * i).ToArray()),
        };
        var members = new Dictionary<string, decimal> { ["A"] = 100m, ["B"] = 100m };

        // only two members with data -> null
        Assert.Null(OutcomeMath.SectorMedianForwardReturn(members, closesByTicker, Fire, 30));

        closesByTicker["C"] = Daily("C", Fire, Enumerable.Range(0, 31).Select(i => 100m - i).ToArray());
        members["C"] = 100m;
        // returns: +30, +60, -30 -> median +30
        Assert.Equal(30m, OutcomeMath.SectorMedianForwardReturn(members, closesByTicker, Fire, 30));
    }

    [Fact]
    public void SectorMedian_skips_members_whose_history_is_missing()
    {
        var closesByTicker = new Dictionary<string, List<DailyCloseRow>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Daily("A", Fire, Enumerable.Range(0, 31).Select(i => 100m + i).ToArray()),
        };
        var members = new Dictionary<string, decimal> { ["A"] = 100m, ["GONE"] = 50m, ["GONE2"] = 60m };
        Assert.Null(OutcomeMath.SectorMedianForwardReturn(members, closesByTicker, Fire, 30));
    }
}
