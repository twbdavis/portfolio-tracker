using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Core.Tests;

public class PortfolioCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 14, 41, 0, TimeSpan.FromHours(-4));

    private static readonly IReadOnlyDictionary<string, TickerTarget> NoTargets =
        new Dictionary<string, TickerTarget>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, TickerTarget> Target(string ticker, decimal price) =>
        new Dictionary<string, TickerTarget>(StringComparer.OrdinalIgnoreCase)
        { [ticker] = new TickerTarget { Ticker = ticker, TargetPrice = price } };

    private static (List<Position>, List<Lot>) OnePosition(decimal shares, decimal cost)
    {
        var pos = new Position { Id = 1, AccountId = 1, Ticker = "MU" };
        var lot = new Lot { PositionId = 1, Shares = shares, TotalCost = cost, RemainingShares = shares, RemainingCost = cost, TradeDate = new DateOnly(2026, 1, 5) };
        return (new List<Position> { pos }, new List<Lot> { lot });
    }

    [Fact]
    public void Priced_position_computes_all_derived_fields()
    {
        var (ps, lots) = OnePosition(10m, 1000m);
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase) { ["MU"] = new("MU", 120m, Now) };
        var row = PortfolioCalculator.BuildRows(ps, lots, new(), quotes, Target("MU", 150m)).Single();
        Assert.Equal(10m, row.Shares);
        Assert.Equal(100m, row.AvgCost);
        Assert.Equal(1200m, row.MarketValue);
        Assert.Equal(200m, row.GainLoss);
        Assert.Equal(20m, row.GainLossPct);
        Assert.Equal(25m, row.DistanceToTargetPct);   // (150-120)/120*100
        Assert.Equal(Now, row.PriceAsOf);
        Assert.False(row.PriceUnavailable);
    }

    [Fact]
    public void Day_gain_loss_computed_from_prev_close_and_null_without_it()
    {
        var (ps, lots) = OnePosition(10m, 1000m);
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase) { ["MU"] = new("MU", 120m, Now, PrevClose: 115m) };
        var row = PortfolioCalculator.BuildRows(ps, lots, new(), quotes, NoTargets).Single();
        Assert.Equal(50m, row.DayGainLoss);           // 10 * (120-115)

        quotes["MU"] = new("MU", 120m, Now);          // cached quote from before PrevClose existed
        row = PortfolioCalculator.BuildRows(ps, lots, new(), quotes, NoTargets).Single();
        Assert.Null(row.DayGainLoss);
    }

    [Fact]
    public void Missing_quote_leaves_market_fields_null_and_flags_row()
    {
        var (ps, lots) = OnePosition(10m, 1000m);
        var row = PortfolioCalculator.BuildRows(ps, lots, new(), new Dictionary<string, PriceQuote>(), NoTargets).Single();
        Assert.Null(row.CurrentPrice);
        Assert.Null(row.MarketValue);
        Assert.Null(row.GainLoss);
        Assert.True(row.PriceUnavailable);
        Assert.Equal(100m, row.AvgCost); // cost math still works offline
    }

    [Fact]
    public void Unmatched_shares_reduce_share_count()
    {
        var (ps, lots) = OnePosition(10m, 1000m);
        var row = PortfolioCalculator.BuildRows(ps, lots, new() { [1L] = 3m }, new Dictionary<string, PriceQuote>(), NoTargets).Single();
        Assert.Equal(7m, row.Shares);
    }

    [Fact]
    public void Closed_position_has_zero_shares_and_null_avg_cost()
    {
        var ps = new List<Position> { new() { Id = 1, Ticker = "RCAT" } };
        var row = PortfolioCalculator.BuildRows(ps, new List<Lot>(), new(), new Dictionary<string, PriceQuote>(), NoTargets).Single();
        Assert.Equal(0m, row.Shares);
        Assert.Null(row.AvgCost);
    }

    [Fact]
    public void Realized_gain_loss_maps_per_position_including_closed_ones()
    {
        var ps = new List<Position>
        {
            new() { Id = 1, Ticker = "BSV" },   // closed, sold at a loss
            new() { Id = 2, Ticker = "NVDA" },  // never sold
        };
        var realized = new Dictionary<long, decimal> { [1L] = -4.64m };
        var rows = PortfolioCalculator.BuildRows(ps, new List<Lot>(), new(), new Dictionary<string, PriceQuote>(), NoTargets, realized);
        Assert.Equal(-4.64m, rows.Single(r => r.Ticker == "BSV").RealizedGainLoss);
        Assert.Null(rows.Single(r => r.Ticker == "NVDA").RealizedGainLoss);
    }

    // ---- cumulative "All accounts" aggregation ------------------------------

    private static Dictionary<long, Account> TwoAccounts() => new()
    {
        [1] = new Account { Id = 1, AccountNumber = "...1234", DisplayName = "Taxable" },
        [2] = new Account { Id = 2, AccountNumber = "...5678", DisplayName = "Roth", TaxTreatment = TaxTreatment.RothIRA },
    };

    [Fact]
    public void Cumulative_row_blends_avg_cost_across_accounts()
    {
        // VOO in two accounts: 1 sh @ 505.31 taxable + 0.2 sh @ 102.20 Roth
        var positions = new List<Position>
        {
            new() { Id = 10, AccountId = 1, Ticker = "VOO" },
            new() { Id = 20, AccountId = 2, Ticker = "VOO" },
        };
        var lots = new List<Lot>
        {
            new() { PositionId = 10, Shares = 1m, TotalCost = 505.31m, RemainingShares = 1m, RemainingCost = 505.31m, TradeDate = new(2026, 5, 20) },
            new() { PositionId = 20, Shares = 0.2m, TotalCost = 102.20m, RemainingShares = 0.2m, RemainingCost = 102.20m, TradeDate = new(2026, 6, 12) },
        };
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase) { ["VOO"] = new("VOO", 510m, Now) };
        var row = PortfolioCalculator.BuildCumulativeRows(positions, lots, new(), quotes, NoTargets, TwoAccounts()).Single();

        Assert.Equal(1.2m, row.Shares);
        Assert.Equal(607.51m, row.CostBasis);
        Assert.Equal(607.51m / 1.2m, row.AvgCost);       // blended: total cost / total shares
        Assert.Equal(1.2m * 510m, row.MarketValue);      // from aggregates
        Assert.Equal(1.2m * 510m - 607.51m, row.GainLoss);
        Assert.True(row.HeldInIra);
        Assert.Contains("Roth IRA ...5678", row.IraNote);
    }

    [Fact]
    public void Cumulative_row_for_single_account_ticker_matches_account_row()
    {
        var positions = new List<Position> { new() { Id = 10, AccountId = 1, Ticker = "MU" } };
        var lots = new List<Lot>
        {
            new() { PositionId = 10, Shares = 10m, TotalCost = 1000m, RemainingShares = 10m, RemainingCost = 1000m, TradeDate = new(2026, 1, 5) },
        };
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase) { ["MU"] = new("MU", 120m, Now) };
        var single = PortfolioCalculator.BuildRows(positions, lots, new(), quotes, NoTargets).Single();
        var cumulative = PortfolioCalculator.BuildCumulativeRows(positions, lots, new(), quotes, NoTargets, TwoAccounts()).Single();
        Assert.Equal(single.Shares, cumulative.Shares);
        Assert.Equal(single.AvgCost, cumulative.AvgCost);
        Assert.Equal(single.MarketValue, cumulative.MarketValue);
        Assert.False(cumulative.HeldInIra);
    }

    [Fact]
    public void Cumulative_target_columns_come_from_ticker_target()
    {
        var positions = new List<Position>
        {
            new() { Id = 10, AccountId = 1, Ticker = "VOO" },
            new() { Id = 20, AccountId = 2, Ticker = "VOO" },
        };
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase) { ["VOO"] = new("VOO", 500m, Now) };
        var row = PortfolioCalculator.BuildCumulativeRows(positions, new List<Lot>(), new(), quotes, Target("VOO", 550m), TwoAccounts()).Single();
        Assert.Equal(550m, row.TargetPrice);
        Assert.Equal(10m, row.DistanceToTargetPct);      // (550-500)/500*100
    }

    [Fact]
    public void Summary_totals_priced_open_rows_and_counts_unpriced()
    {
        var rows = new List<PositionRow>
        {
            new() { Ticker = "A", Shares = 10m, CostBasis = 1000m, CurrentPrice = 120m, MarketValue = 1200m, GainLoss = 200m, DayGainLoss = 60m },
            new() { Ticker = "B", Shares = 5m, CostBasis = 500m, PriceUnavailable = true },
            new() { Ticker = "C", Shares = 0m, CostBasis = 0m },  // closed: excluded
        };
        var s = PortfolioCalculator.Summarize(rows);
        Assert.Equal(1200m, s.TotalMarketValue);
        Assert.Equal(1500m, s.TotalCostBasis);
        Assert.Equal(200m, s.TotalGainLoss);
        Assert.Equal(20m, s.TotalGainLossPct);        // vs priced cost basis 1000
        Assert.Equal(60m, s.TotalDayGainLoss);
        Assert.Equal(1, s.UnpricedCount);
    }

    [Fact]
    public void Summary_day_gain_loss_null_when_no_row_has_prev_close()
    {
        var rows = new List<PositionRow>
        {
            new() { Ticker = "A", Shares = 10m, CostBasis = 1000m, CurrentPrice = 120m, MarketValue = 1200m, GainLoss = 200m },
        };
        var s = PortfolioCalculator.Summarize(rows);
        Assert.Null(s.TotalDayGainLoss);
        Assert.Null(s.TotalDayGainLossPct);
    }

    [Fact]
    public void Summary_day_gain_loss_pct_uses_prior_day_value()
    {
        var rows = new List<PositionRow>
        {
            // prior-day value = 1200 - 60 = 1140; 60 / 1140 * 100
            new() { Ticker = "A", Shares = 10m, CostBasis = 1000m, CurrentPrice = 120m, MarketValue = 1200m, GainLoss = 200m, DayGainLoss = 60m },
        };
        var s = PortfolioCalculator.Summarize(rows);
        Assert.Equal(60m, s.TotalDayGainLoss);
        Assert.Equal(60m / 1140m * 100m, s.TotalDayGainLossPct);
    }
}
