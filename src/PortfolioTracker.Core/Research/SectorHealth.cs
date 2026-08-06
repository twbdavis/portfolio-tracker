namespace PortfolioTracker.Core.Research;

public enum SectorQuadrant { HealthyRally, Recovering, Topping, Breakdown }

/// Sector health quadrant derived from the breadth dashboard's two axes:
/// participation (share of tickers above their SMA50) and momentum (median
/// 20-day return). The mapping is deliberately simple and printed with every
/// queue row, so it reads as evidence, not a score:
///
///   breadth ≥ 50 & momentum ≥ 0  → HealthyRally   (broad participation, rising)
///   breadth < 50 & momentum ≥ 0  → Recovering     (rising on narrow breadth)
///   breadth ≥ 50 & momentum < 0  → Topping        (broad but rolling over)
///   breadth < 50 & momentum < 0  → Breakdown      (narrow and falling)
public static class SectorHealth
{
    public const decimal BreadthThresholdPct = 50m;

    public static SectorQuadrant? Classify(decimal? breadthAboveSma50Pct, decimal? medianReturn20d)
    {
        if (breadthAboveSma50Pct is not decimal breadth || medianReturn20d is not decimal ret)
            return null;
        var broad = breadth >= BreadthThresholdPct;
        var rising = ret >= 0m;
        return (broad, rising) switch
        {
            (true, true) => SectorQuadrant.HealthyRally,
            (false, true) => SectorQuadrant.Recovering,
            (true, false) => SectorQuadrant.Topping,
            (false, false) => SectorQuadrant.Breakdown,
        };
    }
}
