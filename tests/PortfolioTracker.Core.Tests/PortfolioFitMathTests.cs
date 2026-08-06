using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;

namespace PortfolioTracker.Core.Tests;

public class PortfolioFitMathTests
{
    private static List<DailyCloseRow> Closes(string ticker, DateOnly start, params decimal[] closes)
    {
        var list = new List<DailyCloseRow>();
        var date = start;
        foreach (var c in closes)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
            list.Add(new DailyCloseRow { Ticker = ticker, Date = date, Close = c });
            date = date.AddDays(1);
        }
        return list;
    }

    private static readonly DateOnly Start = new(2026, 1, 5);   // a Monday

    // ---- weights ---------------------------------------------------------------

    [Fact]
    public void Weights_are_value_proportional_and_aggregate_across_accounts()
    {
        var weights = PortfolioFitMath.Weights(new[]
        {
            ("NVDA", 10m, 100m),   // 1000
            ("nvda", 10m, 100m),   // same ticker, second account -> 2000 total
            ("MU", 20m, 100m),     // 2000
        });
        Assert.Equal(2, weights.Count);
        Assert.Equal(0.5m, weights.Single(w => w.Ticker == "NVDA").Weight);
        Assert.Equal(0.5m, weights.Single(w => w.Ticker == "MU").Weight);
    }

    [Fact]
    public void Weights_drop_zero_value_holdings_and_handle_empty_portfolio()
    {
        var weights = PortfolioFitMath.Weights(new[] { ("NVDA", 10m, 0m) });   // no cached price
        Assert.Empty(weights);
        Assert.Empty(PortfolioFitMath.Weights(Array.Empty<(string, decimal, decimal)>()));
    }

    // ---- composite returns --------------------------------------------------------

    [Fact]
    public void Composite_is_value_weighted_and_renormalizes_over_missing_days()
    {
        var closes = new Dictionary<string, List<DailyCloseRow>>(StringComparer.OrdinalIgnoreCase)
        {
            // A: +10% then +10%
            ["A"] = Closes("A", Start, 100m, 110m, 121m),
            // B: only two bars -> return exists only on day 2
            ["B"] = Closes("B", Start, 200m, 220m),
        };
        var weights = new List<HoldingWeight> { new("A", 0.75m), new("B", 0.25m) };
        var composite = PortfolioFitMath.CompositeReturns(closes, weights);

        Assert.Equal(2, composite.Count);
        // day 2: both present -> 0.75*0.10 + 0.25*0.10 = 0.10
        Assert.Equal(0.10m, Math.Round(composite[Start.AddDays(1)], 6));
        // day 3: only A -> its return alone (weights renormalize), not 0.75*0.10
        Assert.Equal(0.10m, Math.Round(composite[Start.AddDays(2)], 6));
    }

    [Fact]
    public void Single_holding_portfolio_composite_is_that_stock()
    {
        var closes = new Dictionary<string, List<DailyCloseRow>>
        {
            ["A"] = Closes("A", Start, 100m, 105m, 110.25m),
        };
        var composite = PortfolioFitMath.CompositeReturns(closes,
            new List<HoldingWeight> { new("A", 1m) });
        Assert.All(composite.Values, r => Assert.Equal(0.05m, Math.Round(r, 6)));
    }

    // ---- pearson --------------------------------------------------------------------

    [Fact]
    public void Pearson_is_one_for_identical_series_and_minus_one_for_inverted()
    {
        var up = new List<(decimal, decimal)> { (1m, 1m), (2m, 2m), (3m, 3m), (4m, 4m) };
        Assert.Equal(1m, PortfolioFitMath.Pearson(up));

        var inverse = new List<(decimal, decimal)> { (1m, -1m), (2m, -2m), (3m, -3m) };
        Assert.Equal(-1m, PortfolioFitMath.Pearson(inverse));
    }

    [Fact]
    public void Pearson_is_null_for_zero_variance_series()
    {
        var flat = new List<(decimal, decimal)> { (1m, 5m), (2m, 5m), (3m, 5m) };
        Assert.Null(PortfolioFitMath.Pearson(flat));
    }

    // ---- correlate ---------------------------------------------------------------------

    [Fact]
    public void Correlate_returns_null_with_observation_count_below_minimum()
    {
        var candidate = Closes("C", Start, Enumerable.Range(0, 30).Select(i => 100m + i).ToArray());
        var composite = PortfolioFitMath.CompositeReturns(
            new Dictionary<string, List<DailyCloseRow>> { ["C"] = candidate },
            new List<HoldingWeight> { new("C", 1m) });

        var (corr, obs) = PortfolioFitMath.Correlate(candidate, composite, minObservations: 60);
        Assert.Null(corr);
        Assert.Equal(29, obs);
    }

    [Fact]
    public void Candidate_that_is_the_single_holding_correlates_at_one()
    {
        var series = Closes("A", Start,
            Enumerable.Range(0, 80).Select(i => 100m + (i % 7) * 3m).ToArray());
        var composite = PortfolioFitMath.CompositeReturns(
            new Dictionary<string, List<DailyCloseRow>> { ["A"] = series },
            new List<HoldingWeight> { new("A", 1m) });

        var (corr, obs) = PortfolioFitMath.Correlate(series, composite, minObservations: 60);
        Assert.Equal(1m, corr);
        Assert.Equal(79, obs);
    }

    [Fact]
    public void Correlate_uses_only_the_trailing_window_of_the_composite()
    {
        var series = Closes("A", Start,
            Enumerable.Range(0, 200).Select(i => 100m + (i % 5) * 2m).ToArray());
        var composite = PortfolioFitMath.CompositeReturns(
            new Dictionary<string, List<DailyCloseRow>> { ["A"] = series },
            new List<HoldingWeight> { new("A", 1m) });

        var (_, obs) = PortfolioFitMath.Correlate(series, composite,
            windowSessions: 90, minObservations: 60);
        Assert.Equal(90, obs);   // capped by the window, not the 199 available returns
    }

    [Fact]
    public void Correlate_with_empty_composite_or_no_candidate_history_is_na()
    {
        var empty = new SortedDictionary<DateOnly, decimal>();
        Assert.Equal((null, 0), PortfolioFitMath.Correlate(new List<DailyCloseRow>(), empty));

        var composite = new SortedDictionary<DateOnly, decimal> { [Start] = 0.01m };
        var (corr, obs) = PortfolioFitMath.Correlate(new List<DailyCloseRow>(), composite);
        Assert.Null(corr);
        Assert.Equal(0, obs);
    }

    // ---- sector weights + fit line ---------------------------------------------------------

    [Fact]
    public void SectorWeights_group_and_order_by_share()
    {
        var weights = new List<HoldingWeight> { new("NVDA", 0.5m), new("MU", 0.3m), new("XOM", 0.2m) };
        var sectors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NVDA"] = "Technology", ["MU"] = "Technology", ["XOM"] = "Energy",
        };
        var result = PortfolioFitMath.SectorWeights(weights, sectors);
        Assert.Equal(("Technology", 80m), (result[0].Sector, Math.Round(result[0].WeightPct, 2)));
        Assert.Equal(("Energy", 20m), (result[1].Sector, Math.Round(result[1].WeightPct, 2)));
    }

    [Fact]
    public void FitLine_describes_correlation_level_and_sector_exposure()
    {
        Assert.Equal("Low correlation (0.21) to your portfolio; you have no Materials exposure.",
            PortfolioFitMath.FitLine(0.21m, 70, "Materials", 0m, 25m));
        Assert.StartsWith("High correlation (0.78)",
            PortfolioFitMath.FitLine(0.78m, 70, "Technology", 42m, 25m));
        Assert.Contains("Technology is already 42% of your portfolio",
            PortfolioFitMath.FitLine(0.78m, 70, "Technology", 42m, 25m));
        Assert.Contains("Not enough overlapping history to correlate (12 sessions)",
            PortfolioFitMath.FitLine(null, 12, "Energy", 5m, 25m));
    }
}
