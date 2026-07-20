using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Tests;

public class RobustStatsTests
{
    [Fact]
    public void Median_odd_and_even_counts()
    {
        Assert.Equal(3m, RobustStats.Median(new[] { 1m, 3m, 5m }));
        Assert.Equal(2.5m, RobustStats.Median(new[] { 1m, 2m, 3m, 4m }));
        Assert.Equal(7m, RobustStats.Median(new[] { 7m }));
        Assert.Null(RobustStats.Median(Array.Empty<decimal>()));
    }

    [Fact]
    public void Median_is_order_independent()
    {
        Assert.Equal(3m, RobustStats.Median(new[] { 5m, 1m, 3m }));
    }

    [Fact]
    public void Mad_basics()
    {
        // median 3, |x-3| = {2,1,0,1,2}, median = 1
        Assert.Equal(1m, RobustStats.Mad(new[] { 1m, 2m, 3m, 4m, 5m }));
        Assert.Equal(0m, RobustStats.Mad(new[] { 4m, 4m, 4m }));
        Assert.Null(RobustStats.Mad(Array.Empty<decimal>()));
    }

    [Fact]
    public void RobustZ_centers_on_median_and_scales_by_mad()
    {
        var pop = new[] { 1m, 2m, 3m, 4m, 5m };
        Assert.Equal(0m, RobustStats.RobustZ(3m, pop));
        var z5 = RobustStats.RobustZ(5m, pop)!.Value;
        Assert.Equal(2m / (RobustStats.MadToSigma * 1m), z5, 6);
        Assert.True(RobustStats.RobustZ(1m, pop) < 0m);
    }

    [Fact]
    public void RobustZ_ignores_one_wild_outlier()
    {
        // mean/stdev z of 4 would be dragged down by the 1000; robust z barely moves
        var clean = RobustStats.RobustZ(4m, new[] { 1m, 2m, 3m, 4m, 5m })!.Value;
        var withOutlier = RobustStats.RobustZ(4m, new[] { 1m, 2m, 3m, 4m, 1000m })!.Value;
        Assert.Equal(clean, withOutlier, 3);
    }

    [Fact]
    public void Sector_of_one_or_two_yields_null_z()
    {
        Assert.Null(RobustStats.RobustZ(5m, new[] { 5m }));
        Assert.Null(RobustStats.RobustZ(5m, new[] { 5m, 6m }));
    }

    [Fact]
    public void Zero_variance_yields_null_z_not_divide_by_zero()
    {
        Assert.Null(RobustStats.RobustZ(4m, new[] { 4m, 4m, 4m, 4m }));
        // even a value away from the constant population: MAD 0 = no scale
        Assert.Null(RobustStats.RobustZ(9m, new[] { 4m, 4m, 4m, 4m }));
    }

    [Fact]
    public void Percentile_basics()
    {
        var pop = new[] { 10m, 20m, 30m, 40m };
        Assert.Equal(25m, RobustStats.Percentile(10m, pop));
        Assert.Equal(100m, RobustStats.Percentile(40m, pop));
        Assert.Equal(50m, RobustStats.Percentile(25m, pop));
        Assert.Null(RobustStats.Percentile(10m, new[] { 10m }));
        Assert.Null(RobustStats.Percentile(10m, Array.Empty<decimal>()));
    }
}

public class PriceDerivedTests
{
    private static List<PortfolioTracker.Core.Models.DailyCloseRow> Series(params decimal[] closes)
    {
        var date = new DateOnly(2025, 1, 2);
        var list = new List<PortfolioTracker.Core.Models.DailyCloseRow>();
        foreach (var c in closes)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
            list.Add(new PortfolioTracker.Core.Models.DailyCloseRow { Ticker = "T", Date = date, Close = c });
            date = date.AddDays(1);
        }
        return list;
    }

    private static List<PortfolioTracker.Core.Models.DailyCloseRow> Flat(int n, decimal value) =>
        Series(Enumerable.Repeat(value, n).ToArray());

    [Fact]
    public void Empty_series_yields_empty_derived()
    {
        var d = PriceDerived.Compute(new List<PortfolioTracker.Core.Models.DailyCloseRow>());
        Assert.Null(d.Price);
        Assert.Null(d.Return5d);
        Assert.Equal(SmaCrossState.Unknown, d.CrossState);
        Assert.Equal(0, d.BarCount);
    }

    [Fact]
    public void Returns_need_n_plus_one_bars()
    {
        var d5 = PriceDerived.Compute(Series(100m, 101m, 102m, 103m, 104m, 110m));   // 6 bars
        Assert.Equal(10m, d5.Return5d);
        Assert.Null(d5.Return20d);

        var d4 = PriceDerived.Compute(Series(100m, 101m, 102m, 103m, 110m));   // 5 bars: not enough
        Assert.Null(d4.Return5d);
    }

    [Fact]
    public void Sma_needs_n_bars()
    {
        var d = PriceDerived.Compute(Flat(50, 10m));
        Assert.Equal(10m, d.Sma50);
        Assert.Null(d.Sma200);

        Assert.Null(PriceDerived.Compute(Flat(49, 10m)).Sma50);
    }

    [Fact]
    public void Insufficient_history_gives_unknown_cross_not_none()
    {
        Assert.Equal(SmaCrossState.Unknown, PriceDerived.Compute(Flat(150, 10m)).CrossState);
    }

    [Fact]
    public void Flat_long_series_has_no_recent_cross()
    {
        Assert.Equal(SmaCrossState.None, PriceDerived.Compute(Flat(260, 10m)).CrossState);
    }

    [Fact]
    public void Golden_cross_detected_within_lookback()
    {
        // high base, decline (SMA50 sinks below SMA200), then a rally strong
        // enough that SMA50 re-crosses SMA200 ~2 sessions before the end
        var closes = Enumerable.Repeat(150m, 200)
            .Concat(Enumerable.Repeat(100m, 55))
            .Concat(Enumerable.Repeat(200m, 23)).ToArray();
        var d = PriceDerived.Compute(Series(closes));
        Assert.Equal(SmaCrossState.GoldenCross, d.CrossState);
    }

    [Fact]
    public void Death_cross_detected_within_lookback()
    {
        var closes = Enumerable.Repeat(100m, 200)
            .Concat(Enumerable.Repeat(150m, 55))
            .Concat(Enumerable.Repeat(50m, 23)).ToArray();
        var d = PriceDerived.Compute(Series(closes));
        Assert.Equal(SmaCrossState.DeathCross, d.CrossState);
    }

    [Fact]
    public void Old_cross_outside_lookback_is_none()
    {
        // rally long ago: the cross happened ~30 sessions back, not in the last 5
        var closes = Enumerable.Repeat(100m, 220).Concat(Enumerable.Repeat(140m, 80)).ToArray();
        var d = PriceDerived.Compute(Series(closes));
        Assert.Equal(SmaCrossState.None, d.CrossState);
    }

    [Fact]
    public void PctOff52wHigh_prefers_metric_falls_back_to_long_series_max()
    {
        var d = PriceDerived.Compute(Series(100m, 90m), wk52High: 200m);
        Assert.Equal(55m, d.PctOff52wHigh);   // (200-90)/200

        // no metric, short series: no fake 52w number
        Assert.Null(PriceDerived.Compute(Series(100m, 90m)).PctOff52wHigh);

        // no metric, long series: max of last 252 bars
        var longSeries = Enumerable.Repeat(100m, 250).Concat(new[] { 80m }).ToArray();
        Assert.Equal(20m, PriceDerived.Compute(Series(longSeries)).PctOff52wHigh);
    }

    [Fact]
    public void AboveSma50Series_flags_and_length_guard()
    {
        Assert.Null(PriceDerived.AboveSma50Series(Flat(58, 10m), 10));   // needs 59
        var flags = PriceDerived.AboveSma50Series(Flat(59, 10m), 10);
        Assert.NotNull(flags);
        Assert.Equal(10, flags!.Count);
        Assert.All(flags, f => Assert.False(f));   // flat: price == SMA, not above

        var rising = Series(Enumerable.Range(1, 70).Select(i => (decimal)i).ToArray());
        var risingFlags = PriceDerived.AboveSma50Series(rising, 10);
        Assert.All(risingFlags!, Assert.True);
    }
}
