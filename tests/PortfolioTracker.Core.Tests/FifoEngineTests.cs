using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Core.Tests;

public class FifoEngineTests
{
    private static Transaction Buy(string date, decimal shares, decimal cost, long id = 0) => new()
    { Id = id, Ticker = "T", TradeDate = DateOnly.Parse(date), Type = TxType.Buy, Shares = shares, Amount = cost, Source = LotSource.ChaseImport };

    private static Transaction Sell(string date, decimal shares, decimal proceeds) => new()
    { Ticker = "T", TradeDate = DateOnly.Parse(date), Type = TxType.Sell, Shares = shares, Amount = proceeds, Source = LotSource.ChaseImport };

    [Fact]
    public void Buys_accumulate_open_lots()
    {
        var o = FifoEngine.Replay(new[] { Buy("2026-01-05", 10m, 100m), Buy("2026-02-05", 0.06235m, 61.37m) });
        Assert.Equal(2, o.Lots.Count);
        Assert.All(o.Lots, l => Assert.Equal(l.Shares, l.RemainingShares));
        Assert.Equal(10.06235m, o.NetShares);
        Assert.False(o.NeedsOpeningBalance);
        Assert.Empty(o.Sales);
    }

    [Fact]
    public void Sell_consumes_oldest_lot_fully()
    {
        var o = FifoEngine.Replay(new[] { Buy("2026-01-05", 10m, 100m), Buy("2026-02-05", 10m, 200m), Sell("2026-03-01", 10m, 150m) });
        Assert.Equal(10m, o.NetShares);
        var jan = o.Lots.Single(l => l.TradeDate == new DateOnly(2026, 1, 5));
        Assert.Equal(0m, jan.RemainingShares);
        var sale = o.Sales.Single();
        Assert.Equal(100m, sale.CostBasisConsumed);
        Assert.Equal(50m, sale.RealizedGainLoss);
        Assert.Equal(0m, sale.UnmatchedShares);
    }

    [Fact]
    public void Partial_sell_splits_lot_with_parent_link_and_proportional_cost()
    {
        var o = FifoEngine.Replay(new[] { Buy("2026-01-05", 10m, 100m), Sell("2026-03-01", 4m, 60m) });
        Assert.Equal(2, o.Lots.Count);                       // consumed parent + open child
        var parent = o.Lots[0];
        var child = o.Lots[1];
        Assert.Equal(0m, parent.RemainingShares);
        Assert.Equal(6m, child.RemainingShares);
        Assert.Equal(60m, child.RemainingCost);              // 100 * 6/10
        Assert.Equal(0L, child.ParentLotId);                 // index of parent in Lots
        Assert.Equal(parent.TradeDate, child.TradeDate);
        Assert.Equal(20m, o.Sales.Single().RealizedGainLoss); // 60 - 40
        Assert.Equal(6m, o.NetShares);
    }

    [Fact]
    public void Sell_spans_multiple_lots_and_split_child_keeps_fifo_priority()
    {
        var o = FifoEngine.Replay(new[] {
            Buy("2026-01-05", 10m, 100m), Buy("2026-02-05", 10m, 200m),
            Sell("2026-03-01", 15m, 300m),                    // eats lot1 + half of lot2 (split)
            Sell("2026-04-01", 3m, 90m) });                   // must eat the *child* of lot2 first
        Assert.Equal(2m, o.NetShares);
        var s1 = o.Sales[0];
        Assert.Equal(100m + 100m, s1.CostBasisConsumed);      // 10@10 + 5@20
        Assert.Equal(100m, s1.RealizedGainLoss);
        var s2 = o.Sales[1];
        Assert.Equal(60m, s2.CostBasisConsumed);              // 3@20 from the split child
        Assert.Equal(30m, s2.RealizedGainLoss);
    }

    [Fact]
    public void Oversell_flags_needs_opening_balance_and_tracks_unmatched()
    {
        var o = FifoEngine.Replay(new[] { Buy("2026-02-05", 5m, 50m), Sell("2026-03-01", 8m, 96m) });
        Assert.True(o.NeedsOpeningBalance);
        Assert.Equal(3m, o.UnmatchedSoldShares);
        Assert.Equal(-3m, o.NetShares);
        var sale = o.Sales.Single();
        Assert.Equal(3m, sale.UnmatchedShares);
        Assert.Equal(50m, sale.CostBasisConsumed);
        Assert.Equal(46m, sale.RealizedGainLoss);             // 96 - 50; approximate until reconciled
    }

    [Fact]
    public void Opening_balance_lot_resolves_prior_oversell_on_replay()
    {
        var o = FifoEngine.Replay(new[] {
            new Transaction { Ticker = "T", TradeDate = new DateOnly(2025, 6, 1), Type = TxType.Buy,
                Shares = 10m, Amount = 80m, Source = LotSource.OpeningBalance },
            Buy("2026-02-05", 5m, 50m), Sell("2026-03-01", 8m, 96m) });
        Assert.False(o.NeedsOpeningBalance);
        Assert.Equal(7m, o.NetShares);
        Assert.Equal(64m, o.Sales.Single().CostBasisConsumed); // 8 * 8.00 from opening lot
    }

    [Fact]
    public void Fractional_shares_keep_five_plus_decimal_places()
    {
        var o = FifoEngine.Replay(new[] { Buy("2026-01-05", 0.06235m, 61.37m), Sell("2026-02-01", 0.03m, 30m) });
        Assert.Equal(0.03235m, o.NetShares);
        var consumed = 61.37m * 0.03m / 0.06235m;
        Assert.Equal(consumed, o.Sales.Single().CostBasisConsumed);
    }
}
