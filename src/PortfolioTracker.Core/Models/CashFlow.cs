namespace PortfolioTracker.Core.Models;

/// Account-level cash movement: an external deposit (BNK) or a cash dividend.
/// Sweep transfers (WDL/DBS) are internal and never recorded.
public class CashFlow
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public DateOnly TradeDate { get; set; }
    public CashFlowType Type { get; set; }
    public string? Ticker { get; set; }      // dividends only
    public decimal Amount { get; set; }      // signed as exported; deposits and dividends are positive
    public string? Description { get; set; }
    public long? ImportBatchId { get; set; }
    public string? DedupeKey { get; set; }
}
