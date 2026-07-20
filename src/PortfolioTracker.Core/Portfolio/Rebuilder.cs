using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Tax;

namespace PortfolioTracker.Core.Portfolio;

public class Rebuilder(AccountRepository accounts, PositionRepository positions, TransactionRepository txs,
                       LotRepository lots, SaleRepository sales,
                       LotConsumptionRepository consumptions, WashSaleAdjustmentRepository washes)
{
    /// Replays one account's transactions for a ticker — FIFO consumption never
    /// crosses accounts — persists derived lots/sales/flags and per-lot
    /// consumption detail, then refreshes wash adjustments for the ticker
    /// across ALL accounts (replacement matching is cross-account).
    public FifoOutcome RebuildTicker(long accountId, string ticker)
    {
        var txList = txs.GetByAccountAndTicker(accountId, ticker); // ordered by TradeDate, Id
        var first = txList.FirstOrDefault();
        var pos = positions.GetOrCreate(accountId, ticker, first?.Cusip, null);
        var outcome = FifoEngine.Replay(txList);
        foreach (var lot in outcome.Lots) { lot.AccountId = accountId; lot.PositionId = pos.Id; }
        foreach (var s in outcome.Sales) { s.AccountId = accountId; s.PositionId = pos.Id; }
        // derived rows reference lots/sales by id: clear them before replacing
        // their targets. Wash adjustments cross accounts (another account's
        // loss may name this account's lot as its replacement), so every
        // position of the ticker clears — RecomputeWashes refills them all.
        foreach (var p in positions.GetAllByTicker(ticker))
            washes.ReplaceForPosition(p.Id, []);
        consumptions.ReplaceForPosition(pos.Id, []);
        var lotIds = lots.ReplaceForPosition(pos.Id, outcome.Lots);
        var saleIds = sales.ReplaceForPosition(pos.Id, outcome.Sales);
        consumptions.ReplaceForPosition(pos.Id, outcome.Consumptions.Select(d => new LotConsumption
        {
            PositionId = pos.Id,
            SaleRecordId = saleIds[d.SaleIndex],
            LotId = lotIds[d.LotIndex],
            SharesConsumed = d.SharesConsumed,
            CostBasisConsumed = d.CostBasisConsumed,
            Proceeds = d.Proceeds,
            LotAcquisitionDate = d.LotAcquisitionDate,
            SaleTradeDate = d.SaleTradeDate,
            HoldingDays = d.HoldingDays,
            Term = d.Term,
            GainLoss = d.GainLoss,
        }).ToList());
        RecomputeWashes(ticker);
        positions.SetNeedsOpeningBalance(pos.Id, outcome.NeedsOpeningBalance);
        return outcome;
    }

    /// Wash sales are derived data at ticker scope: recompute across every
    /// account holding the ticker (IRA lots participate as replacements even
    /// though IRA sales are never taxable events).
    public void RecomputeWashes(string ticker)
    {
        var treatments = accounts.GetAll().ToDictionary(a => a.Id, a => a.TaxTreatment);
        var tickerPositions = positions.GetAllByTicker(ticker);
        if (tickerPositions.Count == 0) return;
        var scope = tickerPositions.ToDictionary(p => p.Id,
            p => new PositionTaxScope(p.AccountId, treatments.GetValueOrDefault(p.AccountId, TaxTreatment.Taxable)));
        var allLots = tickerPositions.SelectMany(p => lots.GetByPosition(p.Id, openOnly: false)).ToList();
        var allCons = tickerPositions.SelectMany(p => consumptions.GetByPosition(p.Id)).ToList();
        var wash = WashSaleEngine.Compute(allLots, allCons, scope, DateTimeOffset.Now);
        foreach (var p in tickerPositions)
            washes.ReplaceForPosition(p.Id, wash.Adjustments.Where(a => a.PositionId == p.Id).ToList());
    }
}
