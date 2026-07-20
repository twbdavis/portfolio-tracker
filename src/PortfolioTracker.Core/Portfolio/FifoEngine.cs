using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Portfolio;

/// One sale's bite out of one lot, referencing lots/sales by their index in
/// FifoOutcome (DB ids don't exist until the outcome is persisted).
public class ConsumptionDraft
{
    public int SaleIndex { get; init; }
    public int LotIndex { get; init; }
    public decimal SharesConsumed { get; init; }
    public decimal CostBasisConsumed { get; init; }
    public decimal Proceeds { get; init; }           // sale proceeds allocated pro-rata by shares
    public DateOnly LotAcquisitionDate { get; init; }
    public DateOnly SaleTradeDate { get; init; }
    public int HoldingDays => SaleTradeDate.DayNumber - LotAcquisitionDate.DayNumber;
    // IRS rule: long-term requires holding MORE than one year — exactly one year is short
    public HoldingTerm Term => TermFor(LotAcquisitionDate, SaleTradeDate);
    public decimal GainLoss => Proceeds - CostBasisConsumed;

    public static HoldingTerm TermFor(DateOnly acquired, DateOnly sold) =>
        sold > acquired.AddYears(1) ? HoldingTerm.LongTerm : HoldingTerm.ShortTerm;
}

public class FifoOutcome
{
    public List<Lot> Lots { get; } = new();          // ALL lots; ParentLotId = parent's index in this list
    public List<SaleRecord> Sales { get; } = new();
    public List<ConsumptionDraft> Consumptions { get; } = new();
    public decimal UnmatchedSoldShares { get; set; }
    public bool NeedsOpeningBalance { get; set; }
    public decimal NetShares => Lots.Sum(l => l.RemainingShares) - UnmatchedSoldShares;
}

public static class FifoEngine
{
    /// Pure FIFO replay. Input must be ordered (TradeDate, then insertion order).
    public static FifoOutcome Replay(IEnumerable<Transaction> orderedTxs)
    {
        var outcome = new FifoOutcome();
        // open lots as indexes into outcome.Lots, oldest first
        var open = new LinkedList<int>();

        foreach (var tx in orderedTxs)
        {
            if (tx.Type == TxType.Buy)
            {
                outcome.Lots.Add(new Lot
                {
                    Shares = tx.Shares, TotalCost = tx.Amount,
                    RemainingShares = tx.Shares, RemainingCost = tx.Amount,
                    TradeDate = tx.TradeDate, Source = tx.Source,
                    ImportBatchId = tx.ImportBatchId, SourceTxId = tx.Id == 0 ? null : tx.Id,
                });
                open.AddLast(outcome.Lots.Count - 1);
                continue;
            }

            // Sell
            decimal toSell = tx.Shares, consumedCost = 0m;
            var saleIndex = outcome.Sales.Count;
            void Record(int lotIdx, decimal shares, decimal cost) =>
                outcome.Consumptions.Add(new ConsumptionDraft
                {
                    SaleIndex = saleIndex, LotIndex = lotIdx,
                    SharesConsumed = shares, CostBasisConsumed = cost,
                    Proceeds = tx.Shares == 0m ? 0m : tx.Amount * shares / tx.Shares,
                    LotAcquisitionDate = outcome.Lots[lotIdx].TradeDate,
                    SaleTradeDate = tx.TradeDate,
                });
            while (toSell > 0m && open.Count > 0)
            {
                var idx = open.First!.Value;
                var lot = outcome.Lots[idx];
                if (lot.RemainingShares <= toSell)
                {
                    Record(idx, lot.RemainingShares, lot.RemainingCost);
                    consumedCost += lot.RemainingCost;
                    toSell -= lot.RemainingShares;
                    lot.RemainingShares = 0m;
                    lot.RemainingCost = 0m;
                    open.RemoveFirst();
                }
                else
                {
                    // split: consume part of this lot, remainder becomes a child lot
                    var costConsumed = lot.RemainingCost * toSell / lot.RemainingShares;
                    var child = new Lot
                    {
                        Shares = lot.RemainingShares - toSell,
                        TotalCost = lot.RemainingCost - costConsumed,
                        RemainingShares = lot.RemainingShares - toSell,
                        RemainingCost = lot.RemainingCost - costConsumed,
                        TradeDate = lot.TradeDate, Source = lot.Source,
                        ImportBatchId = lot.ImportBatchId, SourceTxId = lot.SourceTxId,
                        ParentLotId = idx,
                    };
                    Record(idx, toSell, costConsumed);
                    consumedCost += costConsumed;
                    lot.RemainingShares = 0m;
                    lot.RemainingCost = 0m;
                    outcome.Lots.Add(child);
                    open.RemoveFirst();
                    open.AddFirst(outcome.Lots.Count - 1); // child keeps the parent's age priority
                    toSell = 0m;
                }
            }

            var unmatched = toSell;
            if (unmatched > 0m)
            {
                outcome.UnmatchedSoldShares += unmatched;
                outcome.NeedsOpeningBalance = true;
            }
            outcome.Sales.Add(new SaleRecord
            {
                TradeDate = tx.TradeDate, SharesSold = tx.Shares, Proceeds = tx.Amount,
                CostBasisConsumed = consumedCost,
                RealizedGainLoss = tx.Amount - consumedCost,
                UnmatchedShares = unmatched, ImportBatchId = tx.ImportBatchId,
            });
        }
        return outcome;
    }
}
