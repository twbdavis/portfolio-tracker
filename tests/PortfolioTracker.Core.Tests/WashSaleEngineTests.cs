using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;
using PortfolioTracker.Core.Tax;

namespace PortfolioTracker.Core.Tests;

public class WashSaleEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    /// Replays trades through the real FIFO engine and hands the outcome to the
    /// wash engine the same way Rebuilder does, with synthetic DB ids.
    private static (List<Lot> Lots, List<LotConsumption> Cons, WashSaleResult Result) Run(
        params (DateOnly Date, TxType Type, decimal Shares, decimal Amount)[] txs)
    {
        var outcome = FifoEngine.Replay(txs.Select(t => new Transaction
        {
            Ticker = "T", TradeDate = t.Date, Type = t.Type, Shares = t.Shares, Amount = t.Amount,
        }));
        for (var i = 0; i < outcome.Lots.Count; i++)
        {
            outcome.Lots[i].Id = i + 1;
            outcome.Lots[i].ParentLotId = outcome.Lots[i].ParentLotId is long p ? p + 1 : null;
        }
        var cons = outcome.Consumptions.Select((d, i) => new LotConsumption
        {
            Id = i + 1, PositionId = 1, SaleRecordId = d.SaleIndex + 1, LotId = d.LotIndex + 1,
            SharesConsumed = d.SharesConsumed, CostBasisConsumed = d.CostBasisConsumed,
            Proceeds = d.Proceeds, LotAcquisitionDate = d.LotAcquisitionDate,
            SaleTradeDate = d.SaleTradeDate, HoldingDays = d.HoldingDays, Term = d.Term,
            GainLoss = d.GainLoss,
        }).ToList();
        return (outcome.Lots, cons, WashSaleEngine.Compute(outcome.Lots, cons, Now));
    }

    private static DateOnly D(int m, int d, int y = 2026) => new(y, m, d);

    [Fact]
    public void Loss_fully_replaced_disallows_everything_and_adjusts_replacement_basis()
    {
        var (lots, _, r) = Run(
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 600m),     // 400 loss
            (D(2, 10), TxType.Buy, 10m, 550m));    // replacement inside 30 days after
        var adj = Assert.Single(r.Adjustments);
        Assert.Equal(10m, adj.SharesMatched);
        Assert.Equal(400m, adj.DisallowedLoss);
        var loss = Assert.Single(r.Views);
        Assert.Equal(400m, loss.DisallowedLoss);
        Assert.Equal(0m, loss.AdjustedGainLoss);   // nothing deductible
        var replacement = lots.Single(l => l.TradeDate == D(2, 10));
        Assert.Equal(adj.ReplacementLotId, replacement.Id);
        Assert.Equal(400m, r.OpenLots[replacement.Id].BasisAdjustment);
    }

    [Fact]
    public void Partial_replacement_disallows_proportionally()
    {
        var (_, _, r) = Run(
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 600m),     // 400 loss on 10 shares
            (D(2, 10), TxType.Buy, 4m, 220m));     // only 4 replaced
        var adj = Assert.Single(r.Adjustments);
        Assert.Equal(4m, adj.SharesMatched);
        Assert.Equal(160m, adj.DisallowedLoss);    // 400 * 4/10
        Assert.Equal(-240m, Assert.Single(r.Views).AdjustedGainLoss);
    }

    [Fact]
    public void Replacement_purchased_before_the_sale_also_washes()
    {
        var (_, _, r) = Run(
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(1, 25), TxType.Buy, 5m, 450m),      // within 30 days BEFORE the sale
            (D(2, 2), TxType.Sell, 10m, 600m));    // FIFO consumes the 1/5 lot
        var adj = Assert.Single(r.Adjustments);
        Assert.Equal(5m, adj.SharesMatched);
        Assert.Equal(200m, adj.DisallowedLoss);    // 400 * 5/10
    }

    [Fact]
    public void Multiple_replacement_lots_match_chronologically()
    {
        var (lots, _, r) = Run(
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 600m),     // 400 loss, 40/share... loss per share = 40
            (D(2, 5), TxType.Buy, 4m, 200m),
            (D(2, 20), TxType.Buy, 8m, 400m));
        Assert.Equal(2, r.Adjustments.Count);
        var first = r.Adjustments[0];
        var second = r.Adjustments[1];
        Assert.Equal(lots.Single(l => l.TradeDate == D(2, 5)).Id, first.ReplacementLotId);
        Assert.Equal(4m, first.SharesMatched);
        Assert.Equal(160m, first.DisallowedLoss);
        Assert.Equal(lots.Single(l => l.TradeDate == D(2, 20)).Id, second.ReplacementLotId);
        Assert.Equal(6m, second.SharesMatched);    // only 6 loss shares left to wash
        Assert.Equal(240m, second.DisallowedLoss);
        Assert.Equal(0m, r.Views.Single().AdjustedGainLoss);
    }

    [Fact]
    public void Gains_are_never_washed()
    {
        var (_, _, r) = Run(
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 1400m),    // gain
            (D(2, 10), TxType.Buy, 10m, 1450m));
        Assert.Empty(r.Adjustments);
        Assert.Equal(400m, r.Views.Single().AdjustedGainLoss);
    }

    [Fact]
    public void Window_boundaries_are_inclusive_at_exactly_30_days()
    {
        // sale 3/2: window is 1/31 .. 4/1
        var (_, _, inWindow) = Run(
            (D(1, 1), TxType.Buy, 10m, 1000m),
            (D(3, 2), TxType.Sell, 10m, 600m),
            (D(4, 1), TxType.Buy, 10m, 550m));     // +30 days exactly
        Assert.Single(inWindow.Adjustments);

        var (_, _, outWindow) = Run(
            (D(1, 1), TxType.Buy, 10m, 1000m),
            (D(3, 2), TxType.Sell, 10m, 600m),
            (D(4, 2), TxType.Buy, 10m, 550m));     // +31 days
        Assert.Empty(outWindow.Adjustments);

        var (_, _, before) = Run(
            (D(1, 1, 2025), TxType.Buy, 10m, 1000m),
            (D(1, 31), TxType.Buy, 10m, 900m),     // -30 days exactly
            (D(3, 2), TxType.Sell, 10m, 600m));
        Assert.Single(before.Adjustments);

        var (_, _, tooEarly) = Run(
            (D(1, 1, 2025), TxType.Buy, 10m, 1000m),
            (D(1, 30), TxType.Buy, 10m, 900m),     // -31 days
            (D(3, 2), TxType.Sell, 10m, 600m));
        Assert.Empty(tooEarly.Adjustments);
    }

    [Fact]
    public void Shares_sold_in_the_same_sale_never_replace_themselves()
    {
        var (_, _, r) = Run(
            (D(1, 20), TxType.Buy, 10m, 1000m),    // bought inside its own sale window
            (D(2, 2), TxType.Sell, 10m, 600m));    // sells the whole lot
        Assert.Empty(r.Adjustments);
        Assert.Equal(-400m, r.Views.Single().AdjustedGainLoss);
    }

    [Fact]
    public void Remaining_shares_of_a_recent_lot_do_replace_a_partial_loss_sale()
    {
        var (_, _, r) = Run(
            (D(1, 20), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 4m, 240m));     // sells 4 at a 160 loss; 6 recent shares remain
        var adj = Assert.Single(r.Adjustments);
        Assert.Equal(4m, adj.SharesMatched);
        Assert.Equal(160m, adj.DisallowedLoss);
        Assert.Equal(0m, r.Views.Single().AdjustedGainLoss);
    }

    [Fact]
    public void Each_replacement_share_washes_at_most_one_loss()
    {
        // lots bought long ago (outside each other's windows) so only the 2/10
        // purchase can act as replacement, and only for the first loss
        var (_, _, r) = Run(
            (D(6, 1, 2025), TxType.Buy, 10m, 1000m),
            (D(9, 1, 2025), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 600m),     // loss #1: 400
            (D(2, 3), TxType.Sell, 10m, 600m),     // loss #2: 400
            (D(2, 10), TxType.Buy, 10m, 550m));    // only 10 replacement shares
        // the first (chronological) loss claims the replacement lot; the second finds none
        Assert.Equal(10m, r.Adjustments.Sum(a => a.SharesMatched));
        Assert.Equal(400m, r.Adjustments.Sum(a => a.DisallowedLoss));
        Assert.Equal(-400m, r.Views.Sum(v => v.AdjustedGainLoss));
    }

    [Fact]
    public void Holding_period_tacks_onto_the_replacement_lot()
    {
        // held 349 days -> ST loss; replacement bought 1/10/2026 inherits that period,
        // so selling it on 3/1/2026 (held 50 raw days) is LONG-term with stepped-up basis
        var (_, _, r) = Run(
            (D(1, 5, 2025), TxType.Buy, 10m, 1000m),
            (D(12, 20, 2025), TxType.Sell, 10m, 600m),
            (D(1, 10), TxType.Buy, 10m, 550m),
            (D(3, 1), TxType.Sell, 10m, 700m));
        var second = r.Views.Single(v => v.Source.SaleTradeDate == D(3, 1));
        Assert.Equal(950m, second.AdjustedBasis);          // 550 + 400 disallowed
        Assert.Equal(-250m, second.AdjustedGainLoss);      // 700 - 950
        Assert.Equal(HoldingTerm.LongTerm, second.EffectiveTerm);
        // raw (unadjusted) row stays short-term — originals are never mutated
        Assert.Equal(HoldingTerm.ShortTerm, second.Source.Term);
    }

    [Fact]
    public void Washed_replacement_sold_at_a_loss_chains_into_another_wash()
    {
        var (_, _, r) = Run(
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 600m),     // 400 loss
            (D(2, 10), TxType.Buy, 10m, 550m),     // washes it; basis becomes 950
            (D(3, 1), TxType.Sell, 10m, 700m),     // 250 loss on adjusted basis
            (D(3, 5), TxType.Buy, 10m, 600m));     // washes that too
        var finalLoss = r.Views.Single(v => v.Source.SaleTradeDate == D(3, 1));
        Assert.Equal(250m, finalLoss.DisallowedLoss);
        Assert.Equal(0m, finalLoss.AdjustedGainLoss);
        var lastLot = r.OpenLots.Values.Single();
        Assert.Equal(250m, lastLot.BasisAdjustment);       // 600 + 250 = 850 effective
        Assert.Equal(650m, r.Adjustments.Sum(a => a.DisallowedLoss));
    }

    [Fact]
    public void Split_same_day_sell_rows_never_wash_each_other()
    {
        // Chase splits one liquidation into whole + fractional rows: both leave
        // the position the same day, so neither can act as the other's
        // replacement even though the buy is inside the 61-day window
        var (_, _, r) = Run(
            (D(6, 6), TxType.Buy, 12.8164m, 1013m),
            (D(6, 30), TxType.Sell, 12m, 931.96m),
            (D(6, 30), TxType.Sell, 0.8164m, 63.40m));
        Assert.Empty(r.Adjustments);
        Assert.Equal(2, r.Views.Count);
        Assert.True(r.Views.All(v => v.DisallowedLoss == 0m));
    }

    // ---- cross-account flavors ----------------------------------------------

    private static (List<Lot> Lots, List<LotConsumption> Cons) Replay(
        long positionId, long idOffset,
        params (DateOnly Date, TxType Type, decimal Shares, decimal Amount)[] txs)
    {
        var outcome = FifoEngine.Replay(txs.Select(t => new Transaction
        {
            Ticker = "T", TradeDate = t.Date, Type = t.Type, Shares = t.Shares, Amount = t.Amount,
        }));
        for (var i = 0; i < outcome.Lots.Count; i++)
        {
            outcome.Lots[i].Id = idOffset + i + 1;
            outcome.Lots[i].PositionId = positionId;
            outcome.Lots[i].ParentLotId = outcome.Lots[i].ParentLotId is long p ? idOffset + p + 1 : null;
        }
        var cons = outcome.Consumptions.Select((d, i) => new LotConsumption
        {
            Id = idOffset + i + 1, PositionId = positionId,
            SaleRecordId = idOffset + d.SaleIndex + 1, LotId = idOffset + d.LotIndex + 1,
            SharesConsumed = d.SharesConsumed, CostBasisConsumed = d.CostBasisConsumed,
            Proceeds = d.Proceeds, LotAcquisitionDate = d.LotAcquisitionDate,
            SaleTradeDate = d.SaleTradeDate, HoldingDays = d.HoldingDays, Term = d.Term,
            GainLoss = d.GainLoss,
        }).ToList();
        return (outcome.Lots, cons);
    }

    [Fact]
    public void Cross_account_taxable_replacement_defers_with_kind_and_basis_adjustment()
    {
        var (sellerLots, sellerCons) = Replay(1, 0,
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 600m));
        var (buyerLots, buyerCons) = Replay(2, 100,
            (D(2, 10), TxType.Buy, 10m, 550m));
        var scope = new Dictionary<long, PositionTaxScope>
        {
            [1] = new(AccountId: 1, TaxTreatment.Taxable),
            [2] = new(AccountId: 2, TaxTreatment.Taxable),
        };
        var r = WashSaleEngine.Compute(
            sellerLots.Concat(buyerLots).ToList(), sellerCons.Concat(buyerCons).ToList(), scope, Now);

        var adj = Assert.Single(r.Adjustments);
        Assert.Equal(WashKind.CrossAccountTaxable, adj.Kind);
        Assert.Equal(400m, adj.DisallowedLoss);
        var view = Assert.Single(r.Views);
        Assert.Equal(400m, view.CrossAccountDisallowed);
        Assert.Equal(0m, view.AdjustedGainLoss);
        // basis adjusts on the replacement lot in ITS account; holding period tacks
        Assert.Equal(400m, r.OpenLots[buyerLots.Single().Id].BasisAdjustment);
        Assert.Equal(0m, r.PermanentlyDisallowed);
    }

    [Fact]
    public void Ira_replacement_permanently_disallows_with_no_basis_adjustment_anywhere()
    {
        var (sellerLots, sellerCons) = Replay(1, 0,
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 600m));
        var (iraLots, iraCons) = Replay(2, 100,
            (D(2, 10), TxType.Buy, 10m, 550m));
        var scope = new Dictionary<long, PositionTaxScope>
        {
            [1] = new(AccountId: 1, TaxTreatment.Taxable),
            [2] = new(AccountId: 2, TaxTreatment.RothIRA),
        };
        var r = WashSaleEngine.Compute(
            sellerLots.Concat(iraLots).ToList(), sellerCons.Concat(iraCons).ToList(), scope, Now);

        var adj = Assert.Single(r.Adjustments);
        Assert.Equal(WashKind.IraPermanent, adj.Kind);
        var view = Assert.Single(r.Views);
        Assert.Equal(400m, view.IraDisallowed);
        Assert.Equal(0m, view.AdjustedGainLoss);           // still not deductible
        Assert.Equal(400m, r.PermanentlyDisallowed);
        // Rev. Rul. 2008-5: the deduction is destroyed — nothing to defer
        Assert.Equal(0m, r.OpenLots[iraLots.Single().Id].BasisAdjustment);
        Assert.Equal(D(2, 10), r.OpenLots[iraLots.Single().Id].EffectiveAcquisitionDate); // no tack
    }

    [Fact]
    public void Ira_account_losses_are_not_wash_processed()
    {
        var (iraLots, iraCons) = Replay(1, 0,
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 600m),
            (D(2, 10), TxType.Buy, 10m, 550m));
        var scope = new Dictionary<long, PositionTaxScope> { [1] = new(1, TaxTreatment.RothIRA) };
        var r = WashSaleEngine.Compute(iraLots, iraCons, scope, Now);
        Assert.Empty(r.Adjustments);                       // not a taxable event at all
    }

    [Fact]
    public void Exactly_one_year_is_short_term_one_more_day_is_long_term()
    {
        Assert.Equal(HoldingTerm.ShortTerm, ConsumptionDraft.TermFor(D(3, 10, 2025), D(3, 10)));
        Assert.Equal(HoldingTerm.LongTerm, ConsumptionDraft.TermFor(D(3, 10, 2025), D(3, 11)));
    }

    [Fact]
    public void Sale_spanning_lots_produces_mixed_terms()
    {
        var (_, cons, r) = Run(
            (D(1, 5, 2025), TxType.Buy, 10m, 1000m),
            (D(6, 1), TxType.Buy, 10m, 1200m),
            (D(7, 1), TxType.Sell, 15m, 1800m));   // 10 long-term + 5 short-term
        Assert.Equal(2, cons.Count);
        var lt = r.Views.Single(v => v.EffectiveTerm == HoldingTerm.LongTerm);
        var st = r.Views.Single(v => v.EffectiveTerm == HoldingTerm.ShortTerm);
        Assert.Equal(10m, lt.Source.SharesConsumed);
        Assert.Equal(5m, st.Source.SharesConsumed);
        Assert.Equal(1200m, lt.Source.Proceeds);           // 1800 * 10/15
        Assert.Equal(600m, st.Source.Proceeds);
    }

    [Fact]
    public void Open_lot_states_carry_basis_and_tacked_date_for_the_simulator()
    {
        var (lots, _, r) = Run(
            (D(1, 5), TxType.Buy, 10m, 1000m),
            (D(2, 2), TxType.Sell, 10m, 600m),
            (D(2, 10), TxType.Buy, 10m, 550m));
        var open = r.OpenLots[lots.Single(l => l.TradeDate == D(2, 10)).Id];
        Assert.Equal(400m, open.BasisAdjustment);
        // tacked: 2/10 minus the 28 days the loss shares were held (1/5 -> 2/2)
        Assert.Equal(D(1, 13), open.EffectiveAcquisitionDate);
    }
}
