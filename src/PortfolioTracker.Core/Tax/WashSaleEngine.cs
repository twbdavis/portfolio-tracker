using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tax;

/// Which account a position belongs to and how that account is taxed — the
/// engine's only view of accounts.
public record PositionTaxScope(long AccountId, TaxTreatment Treatment);

/// A consumption row with wash-sale adjustments applied. Raw rows are never
/// mutated; these views are recomputed from lots + consumptions on demand.
public class ConsumptionView
{
    public required LotConsumption Source { get; init; }
    public decimal AdjustedBasis { get; set; }
    public decimal AdjustedGainLoss { get; set; }         // loss reduced by the disallowed portion
    public decimal DisallowedLoss { get; set; }           // positive when part of this loss is washed
    public decimal CrossAccountDisallowed { get; set; }   // deferred into another TAXABLE account's lot
    public decimal IraDisallowed { get; set; }            // Rev. Rul. 2008-5: permanently lost
    public HoldingTerm EffectiveTerm { get; set; }
    public DateOnly EffectiveAcquisitionDate { get; set; }
    public bool IsWash => DisallowedLoss > 0m;
    public bool HasIraWash => IraDisallowed > 0m;
    public bool HasCrossAccountWash => CrossAccountDisallowed > 0m;
}

/// Wash effects carried by a lot that is still open: extra basis from absorbed
/// disallowed losses and the tacked (earlier) effective acquisition date.
public class OpenLotTaxState
{
    public long LotId { get; init; }
    public decimal BasisAdjustment { get; set; }
    public DateOnly EffectiveAcquisitionDate { get; set; }
}

public class WashSaleResult
{
    public List<WashSaleAdjustment> Adjustments { get; } = new();
    public List<ConsumptionView> Views { get; } = new();
    public Dictionary<long, OpenLotTaxState> OpenLots { get; } = new();
    /// Losses destroyed by IRA replacements — never deductible, no basis anywhere.
    public decimal PermanentlyDisallowed => Views.Sum(v => v.IraDisallowed);
}

/// IRS wash sale rule at the TICKER level across every imported account.
/// A taxable-account loss is washed to the extent shares of the same ticker
/// were purchased in the 61-day window (30 days before through 30 days after
/// the sale) in ANY account, excluding the shares sold in that very sale.
/// Replacement lots are matched chronologically; each purchased share can
/// replace at most one washed share. Consequences depend on where the
/// replacement lives:
///  - same or other TAXABLE account: loss deferred — disallowed amount is added
///    to the replacement lot's basis (spread per still-held share) and the sold
///    shares' holding period is tacked onto the lot;
///  - IRA (Rev. Rul. 2008-5): loss PERMANENTLY disallowed — no basis adjustment
///    anywhere, the deduction is destroyed.
/// IRA-account sales are never taxable events, so their losses are not
/// wash-processed at all. Sales are processed chronologically so a washed
/// replacement lot sold later carries its adjusted basis and tacked holding
/// period into that sale, including further washes.
public static class WashSaleEngine
{
    private class RootState
    {
        public decimal BasisDeltaPerShare;                // accumulates as washes attach
        public DateOnly? TackedAcquisitionDate;           // earliest tack wins
        public decimal ConsumedShares;                    // family shares sold so far
        public decimal ReplacementSharesUsed;             // shares already matched to a wash
    }

    /// Single-account convenience: every position is the same taxable account.
    public static WashSaleResult Compute(List<Lot> lots, List<LotConsumption> consumptions, DateTimeOffset now)
    {
        var scope = lots.Select(l => l.PositionId).Concat(consumptions.Select(c => c.PositionId))
            .Distinct().ToDictionary(id => id, _ => new PositionTaxScope(0, TaxTreatment.Taxable));
        return Compute(lots, consumptions, scope, now);
    }

    public static WashSaleResult Compute(List<Lot> lots, List<LotConsumption> consumptions,
        IReadOnlyDictionary<long, PositionTaxScope> scope, DateTimeOffset now)
    {
        var result = new WashSaleResult();
        var byId = lots.ToDictionary(l => l.Id);
        var rootOf = new Dictionary<long, Lot>();
        foreach (var lot in lots)
        {
            var root = lot;
            while (root.ParentLotId is long pid && byId.TryGetValue(pid, out var parent)) root = parent;
            rootOf[lot.Id] = root;
        }
        var roots = lots.Where(l => l.ParentLotId is null).OrderBy(l => l.TradeDate).ThenBy(l => l.Id).ToList();
        var state = roots.ToDictionary(r => r.Id, _ => new RootState());

        // Sales are grouped by DATE, not by sale record: Chase splits one
        // liquidation into whole + fractional rows, and shares leaving in the
        // same-day sibling row must never count as replacement shares.
        foreach (var saleGroup in consumptions
                     .OrderBy(c => c.SaleTradeDate).ThenBy(c => c.SaleRecordId).ThenBy(c => c.Id)
                     .GroupBy(c => c.SaleTradeDate))
        {
            // 1) value every consumption of this sale with the deltas accrued so far
            var saleViews = new List<ConsumptionView>();
            foreach (var c in saleGroup)
            {
                var root = rootOf[c.LotId];
                var s = state[root.Id];
                var effAcq = s.TackedAcquisitionDate is DateOnly t && t < c.LotAcquisitionDate
                    ? t : c.LotAcquisitionDate;
                var adjBasis = c.CostBasisConsumed + s.BasisDeltaPerShare * c.SharesConsumed;
                var view = new ConsumptionView
                {
                    Source = c,
                    AdjustedBasis = adjBasis,
                    AdjustedGainLoss = c.Proceeds - adjBasis,
                    EffectiveTerm = Portfolio.ConsumptionDraft.TermFor(effAcq, c.SaleTradeDate),
                    EffectiveAcquisitionDate = effAcq,
                };
                saleViews.Add(view);
                result.Views.Add(view);
            }

            // 2) the sold shares leave their lots before replacement matching, so
            //    they can never act as their own replacement
            foreach (var c in saleGroup)
                state[rootOf[c.LotId].Id].ConsumedShares += c.SharesConsumed;

            // 3) wash-match each TAXABLE loss portion against window purchases in
            //    every account, earliest purchase first
            foreach (var view in saleViews.Where(v => v.AdjustedGainLoss < 0m
                         && ScopeOf(scope, v.Source.PositionId).Treatment == TaxTreatment.Taxable))
            {
                var c = view.Source;
                var saleScope = ScopeOf(scope, c.PositionId);
                var saleDate = c.SaleTradeDate;
                var windowStart = saleDate.AddDays(-30);
                var windowEnd = saleDate.AddDays(30);
                var lossPerShare = -view.AdjustedGainLoss / c.SharesConsumed;
                var sharesToMatch = c.SharesConsumed;

                foreach (var candidate in roots.Where(r => r.TradeDate >= windowStart && r.TradeDate <= windowEnd))
                {
                    if (sharesToMatch <= 0m) break;
                    var cs = state[candidate.Id];
                    var stillHeld = candidate.Shares - cs.ConsumedShares;
                    var capacity = stillHeld - cs.ReplacementSharesUsed;
                    if (capacity <= 0m) continue;

                    var candidateScope = ScopeOf(scope, candidate.PositionId);
                    var kind = candidateScope.Treatment != TaxTreatment.Taxable ? WashKind.IraPermanent
                        : candidateScope.AccountId != saleScope.AccountId ? WashKind.CrossAccountTaxable
                        : WashKind.SameAccount;

                    var matched = Math.Min(capacity, sharesToMatch);
                    var disallowed = lossPerShare * matched;
                    cs.ReplacementSharesUsed += matched;
                    if (kind == WashKind.IraPermanent)
                    {
                        // the deduction is destroyed: nothing to defer, no tack
                        view.IraDisallowed += disallowed;
                    }
                    else
                    {
                        cs.BasisDeltaPerShare += disallowed / stillHeld;  // spread over still-held family shares
                        var tacked = candidate.TradeDate.AddDays(
                            -(saleDate.DayNumber - view.EffectiveAcquisitionDate.DayNumber));
                        if (cs.TackedAcquisitionDate is not DateOnly existing || tacked < existing)
                            cs.TackedAcquisitionDate = tacked;
                        if (kind == WashKind.CrossAccountTaxable)
                            view.CrossAccountDisallowed += disallowed;
                    }

                    result.Adjustments.Add(new WashSaleAdjustment
                    {
                        PositionId = c.PositionId,
                        LotConsumptionId = c.Id,
                        ReplacementLotId = candidate.Id,
                        SharesMatched = matched,
                        DisallowedLoss = disallowed,
                        Kind = kind,
                        CreatedAt = now,
                    });
                    view.DisallowedLoss += disallowed;
                    sharesToMatch -= matched;
                }
                view.AdjustedGainLoss += view.DisallowedLoss;  // disallowed portion is not a realized loss
            }
        }

        foreach (var lot in lots.Where(l => l.RemainingShares > 0m))
        {
            var s = state[rootOf[lot.Id].Id];
            result.OpenLots[lot.Id] = new OpenLotTaxState
            {
                LotId = lot.Id,
                BasisAdjustment = s.BasisDeltaPerShare * lot.RemainingShares,
                EffectiveAcquisitionDate = s.TackedAcquisitionDate is DateOnly t && t < lot.TradeDate
                    ? t : lot.TradeDate,
            };
        }
        return result;
    }

    private static PositionTaxScope ScopeOf(IReadOnlyDictionary<long, PositionTaxScope> scope, long positionId) =>
        scope.TryGetValue(positionId, out var s) ? s : new PositionTaxScope(0, TaxTreatment.Taxable);
}
