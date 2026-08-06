using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Research;

/// Forward-return maturation for signal outcomes. Pure functions: horizons,
/// baselines and maturity all derive from warehouse closes passed in, so the
/// boundary cases (matures exactly on day N, gap over a weekend, delisted
/// series that never matures) are unit-testable.
public static class OutcomeMath
{
    public static readonly int[] Horizons = { 30, 60, 90 };

    /// The close used as a fire-time baseline: the last close on or before the
    /// fire date. Null when the series has nothing at or before it.
    public static decimal? CloseOnOrBefore(IReadOnlyList<DailyCloseRow> closesOldestFirst, DateOnly date)
    {
        decimal? best = null;
        foreach (var c in closesOldestFirst)
        {
            if (c.Date > date) break;
            best = c.Close;
        }
        return best;
    }

    /// Percent return from the fire baseline to the first close on or after
    /// fireDate + horizonDays. Null while the horizon hasn't matured — i.e.
    /// no close at or past the target date exists yet.
    public static decimal? ForwardReturn(IReadOnlyList<DailyCloseRow> closesOldestFirst,
        DateOnly fireDate, decimal closeAtFire, int horizonDays)
    {
        if (closeAtFire <= 0m) return null;
        var target = fireDate.AddDays(horizonDays);
        foreach (var c in closesOldestFirst)
        {
            if (c.Date < target) continue;
            return (c.Close / closeAtFire - 1m) * 100m;
        }
        return null;
    }

    /// Median forward return of the sector members whose fire-time closes were
    /// snapshotted. Null below three matured members — two names are a pair,
    /// not a sector.
    public static decimal? SectorMedianForwardReturn(
        IReadOnlyDictionary<string, decimal> memberClosesAtFire,
        IReadOnlyDictionary<string, List<DailyCloseRow>> closesByTicker,
        DateOnly fireDate, int horizonDays)
    {
        var returns = new List<decimal>();
        foreach (var (ticker, closeAtFire) in memberClosesAtFire)
        {
            if (!closesByTicker.TryGetValue(ticker, out var closes)) continue;
            if (ForwardReturn(closes, fireDate, closeAtFire, horizonDays) is decimal r)
                returns.Add(r);
        }
        return returns.Count >= 3 ? Sectors.RobustStats.Median(returns) : null;
    }
}
