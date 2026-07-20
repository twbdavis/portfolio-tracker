namespace PortfolioTracker.Core.Models;

public class SaleRecord
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public long PositionId { get; set; }
    public DateOnly TradeDate { get; set; }
    public decimal SharesSold { get; set; }
    public decimal Proceeds { get; set; }
    public decimal CostBasisConsumed { get; set; }
    public decimal RealizedGainLoss { get; set; }
    public decimal UnmatchedShares { get; set; }   // shares sold with no lot to consume (history gap)
    public long? ImportBatchId { get; set; }
}
