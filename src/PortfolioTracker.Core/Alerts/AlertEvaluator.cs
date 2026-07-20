using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Alerts;

public static class AlertEvaluator
{
    /// True when a target-price alert should fire now. Caller stamps AlertTriggeredAt;
    /// a stamped alert never re-fires until the user re-arms or changes the target.
    /// Targets are ticker-level: one evaluation per ticker regardless of accounts.
    public static bool ShouldFire(TickerTarget t, decimal price)
    {
        if (!t.AlertEnabled || t.TargetPrice is not decimal target || t.AlertTriggeredAt is not null)
            return false;
        return t.TargetDirection == TargetDirection.AtOrAbove ? price >= target : price <= target;
    }

    /// Watchlist alerts are always buy targets: fire at-or-below, same one-shot
    /// semantics as positions.
    public static bool ShouldFire(WatchlistItem w, decimal price) =>
        w.AlertEnabled && w.TargetBuyPrice is decimal target && w.AlertTriggeredAt is null
        && price <= target;
}
