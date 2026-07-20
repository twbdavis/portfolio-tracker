using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Tests;

public class SectorAnalyzerTests
{
    private static TickerClassification Class(string ticker, string? sector) => new()
    {
        Ticker = ticker, Sector = sector, UpdatedAt = DateTimeOffset.Now,
    };

    private static TickerMetricsRow Metrics(string ticker, decimal? pe = null, decimal? growth = null,
        decimal? gross = null, decimal? net = null, decimal? roe = null) => new()
    {
        Ticker = ticker, AsOf = DateTimeOffset.Now, Pe = pe, RevenueGrowthYoY = growth,
        GrossMargin = gross, NetMargin = net, Roe = roe,
    };

    private static List<DailyCloseRow> Closes(string ticker, params decimal[] closes)
    {
        var date = new DateOnly(2025, 1, 2);
        var list = new List<DailyCloseRow>();
        foreach (var c in closes)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
            list.Add(new DailyCloseRow { Ticker = ticker, Date = date, Close = c });
            date = date.AddDays(1);
        }
        return list;
    }

    [Fact]
    public void Unclassified_tickers_stay_visible_in_their_own_bucket()
    {
        var groups = SectorAnalyzer.GroupBySector(new[] { "NVDA", "MYSTERY" },
            new[] { Class("NVDA", "Technology") },
            new List<TickerMetricsRow>(),
            new Dictionary<string, List<DailyCloseRow>>());
        Assert.Equal(2, groups.Count);
        Assert.Single(groups["Technology"]);
        Assert.Single(groups[SectorAnalyzer.Unclassified]);
    }

    [Fact]
    public void Sector_of_one_summarizes_without_z_scores()
    {
        var groups = SectorAnalyzer.GroupBySector(new[] { "NVDA" },
            new[] { Class("NVDA", "Technology") },
            new[] { Metrics("NVDA", pe: 40m, growth: 0.5m) },
            new Dictionary<string, List<DailyCloseRow>>());
        var rows = groups["Technology"];

        var summary = SectorAnalyzer.Summarize("Technology", rows);
        Assert.Equal(40m, summary.MedianPe);
        Assert.Equal(1, summary.TickerCount);
        Assert.Null(summary.BreadthAboveSma50);   // no history at all

        var rankings = SectorAnalyzer.Rankings(rows);
        var r = rankings.Single();
        Assert.Equal(40m, r.Pe);
        Assert.Null(r.ZPe);       // population of 1: no z, no crash
        Assert.Null(r.ZGrowth);
    }

    [Fact]
    public void All_null_metrics_etf_sparsity_degrades_to_nulls()
    {
        var groups = SectorAnalyzer.GroupBySector(new[] { "VOO", "SPY", "QQQ" },
            new[] { Class("VOO", "ETF"), Class("SPY", "ETF"), Class("QQQ", "ETF") },
            new[] { Metrics("VOO"), Metrics("SPY"), Metrics("QQQ") },   // rows exist, all metrics null
            new Dictionary<string, List<DailyCloseRow>>());

        var summary = SectorAnalyzer.Summarize("ETF", groups["ETF"]);
        Assert.Null(summary.MedianPe);
        Assert.Null(summary.MedianRevenueGrowth);
        Assert.Equal(3, summary.WithMetrics);

        var rankings = SectorAnalyzer.Rankings(groups["ETF"]);
        Assert.All(rankings, r => Assert.Null(r.ZPe));
        Assert.All(rankings, r => Assert.Null(r.ZGrowth));
    }

    [Fact]
    public void Zero_variance_metric_yields_null_z_for_everyone()
    {
        var groups = SectorAnalyzer.GroupBySector(new[] { "A", "B", "C" },
            new[] { Class("A", "S"), Class("B", "S"), Class("C", "S") },
            new[] { Metrics("A", pe: 20m), Metrics("B", pe: 20m), Metrics("C", pe: 20m) },
            new Dictionary<string, List<DailyCloseRow>>());
        var rankings = SectorAnalyzer.Rankings(groups["S"]);
        Assert.All(rankings, r => Assert.Null(r.ZPe));
    }

    [Fact]
    public void Pe_z_is_inverted_so_cheap_is_positive_and_negative_pe_is_excluded()
    {
        var groups = SectorAnalyzer.GroupBySector(new[] { "CHEAP", "MID", "DEAR", "LOSS" },
            new[] { Class("CHEAP", "S"), Class("MID", "S"), Class("DEAR", "S"), Class("LOSS", "S") },
            new[]
            {
                Metrics("CHEAP", pe: 10m), Metrics("MID", pe: 20m),
                Metrics("DEAR", pe: 40m), Metrics("LOSS", pe: -5m),
            },
            new Dictionary<string, List<DailyCloseRow>>());
        var rankings = SectorAnalyzer.Rankings(groups["S"]).ToDictionary(r => r.Ticker);

        Assert.True(rankings["CHEAP"].ZPe > 0m);
        Assert.True(rankings["DEAR"].ZPe < 0m);
        Assert.Null(rankings["LOSS"].ZPe);   // negative PE is a loss, not cheapness
    }

    [Fact]
    public void Summary_breadth_top_bottom_and_medians()
    {
        // 60 bars each: enough for SMA50 + 20d returns
        var up = Enumerable.Range(1, 60).Select(i => 100m + i).ToArray();      // rising: above SMA50
        var down = Enumerable.Range(1, 60).Select(i => 200m - i * 2).ToArray(); // falling: below SMA50
        var closes = new Dictionary<string, List<DailyCloseRow>>(StringComparer.OrdinalIgnoreCase)
        {
            ["UP"] = Closes("UP", up),
            ["DOWN"] = Closes("DOWN", down),
        };
        var groups = SectorAnalyzer.GroupBySector(new[] { "UP", "DOWN" },
            new[] { Class("UP", "S"), Class("DOWN", "S") },
            new List<TickerMetricsRow>(), closes);

        var summary = SectorAnalyzer.Summarize("S", groups["S"]);
        Assert.Equal(50m, summary.BreadthAboveSma50);
        Assert.Null(summary.BreadthAboveSma200);   // only 60 bars
        Assert.Equal(2, summary.Top5By20d.Count);
        Assert.Equal("UP", summary.Top5By20d[0].Ticker);
        Assert.Equal("DOWN", summary.Bottom5By20d[0].Ticker);
        Assert.NotNull(summary.MedianReturn20d);
    }

    [Fact]
    public void Heatmap_rolls_up_every_sector()
    {
        var groups = SectorAnalyzer.GroupBySector(new[] { "A", "B" },
            new[] { Class("A", "S1"), Class("B", "S2") },
            new List<TickerMetricsRow>(),
            new Dictionary<string, List<DailyCloseRow>>());
        var heatmap = SectorAnalyzer.Heatmap(groups);
        Assert.Equal(2, heatmap.Count);
        Assert.All(heatmap, r => Assert.Equal(1, r.TickerCount));
        Assert.All(heatmap, r => Assert.Null(r.MedianReturn20d));   // no history: nulls, not zeros
    }

    [Fact]
    public void Sectors_lists_coverage_and_freshness()
    {
        var groups = SectorAnalyzer.GroupBySector(new[] { "A", "B", "C" },
            new[] { Class("A", "S"), Class("B", "S"), Class("C", "S") },
            new[] { Metrics("A", pe: 10m) },
            new Dictionary<string, List<DailyCloseRow>>(StringComparer.OrdinalIgnoreCase)
            {
                ["B"] = Closes("B", 1m, 2m, 3m),
            });
        var info = SectorAnalyzer.Sectors(groups).Single();
        Assert.Equal("S", info.Sector);
        Assert.Equal(3, info.TickerCount);
        Assert.Equal(1, info.WithMetrics);
        Assert.Equal(1, info.WithHistory);
        Assert.NotNull(info.NewestMetricsAsOf);
    }
}
