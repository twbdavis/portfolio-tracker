using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Sectors;

public record TickerAnalysis(string Ticker, string Sector, TickerMetricsRow? Metrics, DerivedMetrics Derived);

public record SectorInfo(string Sector, int TickerCount, int WithMetrics, int WithHistory,
    DateTimeOffset? OldestMetricsAsOf, DateTimeOffset? NewestMetricsAsOf);

public record TickerReturn(string Ticker, decimal Return20d);

public record SectorSummary(string Sector, int TickerCount, int WithMetrics, int WithHistory,
    decimal? MedianPe, decimal? MedianPs, decimal? MedianPb, decimal? MedianRevenueGrowth,
    decimal? AvgPctOff52wHigh, decimal? BreadthAboveSma50, decimal? BreadthAboveSma200,
    decimal? MedianReturn20d, List<TickerReturn> Top5By20d, List<TickerReturn> Bottom5By20d,
    DateTimeOffset? OldestMetricsAsOf, DateTimeOffset? NewestMetricsAsOf);

public record SectorTickerRow(string Ticker,
    decimal? MarketCap, decimal? Pe, decimal? Ps, decimal? Pb,
    decimal? RevenueGrowthYoY, decimal? GrossMargin, decimal? NetMargin, decimal? Roe,
    decimal? PctOff52wHigh, decimal? Return20d,
    decimal? ZPe, decimal? ZGrowth, decimal? ZGrossMargin, decimal? ZNetMargin, decimal? ZRoe,
    decimal? ZPctOff52wHigh, decimal? ZReturn20d,
    DateTimeOffset? MetricsAsOf, DateOnly? PriceDate);

public record SectorHeatmapRow(string Sector, int TickerCount,
    decimal? MedianReturn20d, decimal? BreadthAboveSma50, decimal? MedianPctOff52wHigh);

/// Sector rollups over warehouse rows. Pure functions over in-memory inputs:
/// the API assembles repository data and calls in — nothing here touches the
/// network or the clock, so every rollup is reproducible in tests.
public static class SectorAnalyzer
{
    public const string Unclassified = "Unclassified";

    /// Scope tickers grouped by (possibly overridden) sector. Tickers whose
    /// profile hasn't been crawled yet land in "Unclassified" so coverage
    /// gaps stay visible instead of silently vanishing.
    public static Dictionary<string, List<TickerAnalysis>> GroupBySector(
        IReadOnlyCollection<string> scopeTickers,
        IReadOnlyList<TickerClassification> classifications,
        IReadOnlyList<TickerMetricsRow> metrics,
        IReadOnlyDictionary<string, List<DailyCloseRow>> closesByTicker)
    {
        var sectorByTicker = classifications
            .Where(c => !string.IsNullOrWhiteSpace(c.Sector))
            .ToDictionary(c => c.Ticker, c => c.Sector!, StringComparer.OrdinalIgnoreCase);
        var metricsByTicker = metrics.ToDictionary(m => m.Ticker, StringComparer.OrdinalIgnoreCase);

        var groups = new Dictionary<string, List<TickerAnalysis>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticker in scopeTickers.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var sector = sectorByTicker.GetValueOrDefault(ticker, Unclassified);
            var m = metricsByTicker.GetValueOrDefault(ticker);
            var closes = closesByTicker.GetValueOrDefault(ticker) ?? new List<DailyCloseRow>();
            var derived = PriceDerived.Compute(closes, m?.Wk52High);
            if (!groups.TryGetValue(sector, out var list))
                groups[sector] = list = new List<TickerAnalysis>();
            list.Add(new TickerAnalysis(ticker, sector, m, derived));
        }
        return groups;
    }

    public static List<SectorInfo> Sectors(Dictionary<string, List<TickerAnalysis>> groups) =>
        groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SectorInfo(
                g.Key,
                g.Value.Count,
                g.Value.Count(t => t.Metrics is not null),
                g.Value.Count(t => t.Derived.BarCount > 0),
                g.Value.Where(t => t.Metrics is not null).Select(t => t.Metrics!.AsOf).DefaultIfEmpty().Min(),
                g.Value.Where(t => t.Metrics is not null).Select(t => t.Metrics!.AsOf).DefaultIfEmpty().Max()))
            .Select(s => s with
            {
                OldestMetricsAsOf = s.OldestMetricsAsOf == default(DateTimeOffset) ? null : s.OldestMetricsAsOf,
                NewestMetricsAsOf = s.NewestMetricsAsOf == default(DateTimeOffset) ? null : s.NewestMetricsAsOf,
            })
            .ToList();

    public static SectorSummary Summarize(string sector, IReadOnlyList<TickerAnalysis> rows)
    {
        var withSma50 = rows.Where(t => t.Derived.Sma50 is not null && t.Derived.Price is not null).ToList();
        var withSma200 = rows.Where(t => t.Derived.Sma200 is not null && t.Derived.Price is not null).ToList();
        var returns20 = rows.Where(t => t.Derived.Return20d is not null)
            .Select(t => new TickerReturn(t.Ticker, t.Derived.Return20d!.Value))
            .OrderByDescending(r => r.Return20d).ToList();
        var pctOff = Values(rows, t => t.Derived.PctOff52wHigh);
        var metricsAsOf = rows.Where(t => t.Metrics is not null).Select(t => t.Metrics!.AsOf).ToList();

        return new SectorSummary(
            sector,
            rows.Count,
            rows.Count(t => t.Metrics is not null),
            rows.Count(t => t.Derived.BarCount > 0),
            RobustStats.Median(Values(rows, t => t.Metrics?.Pe is > 0m ? t.Metrics.Pe : null)),
            RobustStats.Median(Values(rows, t => t.Metrics?.Ps)),
            RobustStats.Median(Values(rows, t => t.Metrics?.Pb)),
            RobustStats.Median(Values(rows, t => t.Metrics?.RevenueGrowthYoY)),
            pctOff.Count == 0 ? null : pctOff.Average(),
            Breadth(withSma50, t => t.Derived.Price > t.Derived.Sma50),
            Breadth(withSma200, t => t.Derived.Price > t.Derived.Sma200),
            RobustStats.Median(returns20.Select(r => r.Return20d).ToList()),
            returns20.Take(5).ToList(),
            returns20.TakeLast(5).OrderBy(r => r.Return20d).ToList(),
            metricsAsOf.Count == 0 ? null : metricsAsOf.Min(),
            metricsAsOf.Count == 0 ? null : metricsAsOf.Max());
    }

    public static List<SectorTickerRow> Rankings(IReadOnlyList<TickerAnalysis> rows)
    {
        // populations per metric: non-null values within the sector; PE keeps
        // positive values only (a negative PE is a loss, not "cheap")
        var pes = Values(rows, t => t.Metrics?.Pe is > 0m ? t.Metrics.Pe : null);
        var growths = Values(rows, t => t.Metrics?.RevenueGrowthYoY);
        var grossMargins = Values(rows, t => t.Metrics?.GrossMargin);
        var netMargins = Values(rows, t => t.Metrics?.NetMargin);
        var roes = Values(rows, t => t.Metrics?.Roe);
        var pctOffs = Values(rows, t => t.Derived.PctOff52wHigh);
        var returns = Values(rows, t => t.Derived.Return20d);

        return rows.Select(t => new SectorTickerRow(
            t.Ticker,
            t.Metrics?.MarketCap, t.Metrics?.Pe, t.Metrics?.Ps, t.Metrics?.Pb,
            t.Metrics?.RevenueGrowthYoY, t.Metrics?.GrossMargin, t.Metrics?.NetMargin, t.Metrics?.Roe,
            t.Derived.PctOff52wHigh, t.Derived.Return20d,
            // PE inverted so cheap = positive, expensive = negative
            t.Metrics?.Pe is > 0m ? -RobustStats.RobustZ(t.Metrics.Pe.Value, pes) : null,
            Z(t.Metrics?.RevenueGrowthYoY, growths),
            Z(t.Metrics?.GrossMargin, grossMargins),
            Z(t.Metrics?.NetMargin, netMargins),
            Z(t.Metrics?.Roe, roes),
            Z(t.Derived.PctOff52wHigh, pctOffs),
            Z(t.Derived.Return20d, returns),
            t.Metrics?.AsOf, t.Derived.PriceDate)).ToList();
    }

    public static List<SectorHeatmapRow> Heatmap(Dictionary<string, List<TickerAnalysis>> groups) =>
        groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var withSma50 = g.Value.Where(t => t.Derived.Sma50 is not null && t.Derived.Price is not null).ToList();
                return new SectorHeatmapRow(
                    g.Key,
                    g.Value.Count,
                    RobustStats.Median(Values(g.Value, t => t.Derived.Return20d)),
                    Breadth(withSma50, t => t.Derived.Price > t.Derived.Sma50),
                    RobustStats.Median(Values(g.Value, t => t.Derived.PctOff52wHigh)));
            })
            .ToList();

    private static List<decimal> Values(IEnumerable<TickerAnalysis> rows, Func<TickerAnalysis, decimal?> pick) =>
        rows.Select(pick).Where(v => v is not null).Select(v => v!.Value).ToList();

    private static decimal? Z(decimal? value, IReadOnlyCollection<decimal> population) =>
        value is decimal v ? RobustStats.RobustZ(v, population) : null;

    /// Percent of rows satisfying the predicate; null when nothing qualifies
    /// (insufficient history sector-wide).
    private static decimal? Breadth(IReadOnlyList<TickerAnalysis> rows, Func<TickerAnalysis, bool> above) =>
        rows.Count == 0 ? null : (decimal)rows.Count(above) / rows.Count * 100m;
}
