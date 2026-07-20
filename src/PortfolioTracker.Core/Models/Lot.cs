namespace PortfolioTracker.Core.Models;

public class Lot
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public long PositionId { get; set; }
    public decimal Shares { get; set; }            // original shares in this lot row
    public decimal TotalCost { get; set; }         // original cost of this lot row
    public decimal RemainingShares { get; set; }   // 0 when fully consumed by FIFO
    public decimal RemainingCost { get; set; }
    public DateOnly TradeDate { get; set; }
    public LotSource Source { get; set; }
    public long? ImportBatchId { get; set; }
    public long? ParentLotId { get; set; }         // set on the remainder half of a FIFO split
    public long? SourceTxId { get; set; }          // Tx that created this lot (edit/delete manual lots)
}
