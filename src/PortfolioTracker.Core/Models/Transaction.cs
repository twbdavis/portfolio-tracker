namespace PortfolioTracker.Core.Models;

public class Transaction
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Ticker { get; set; } = "";
    public string? Cusip { get; set; }
    public DateOnly TradeDate { get; set; }
    public TxType Type { get; set; }
    public decimal Shares { get; set; }        // always positive
    public decimal Amount { get; set; }        // Buy: total cost; Sell: proceeds. Positive.
    public string? Description { get; set; }   // stored as-is, never parsed
    public LotSource Source { get; set; }
    public long? ImportBatchId { get; set; }
    public string? DedupeKey { get; set; }     // null for Manual/OpeningBalance
}
