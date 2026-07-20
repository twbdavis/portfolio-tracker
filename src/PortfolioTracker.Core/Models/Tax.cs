namespace PortfolioTracker.Core.Models;

public enum HoldingTerm { ShortTerm = 0, LongTerm = 1 }
public enum FilingStatus { Single = 0, MarriedFilingJointly = 1, HeadOfHousehold = 2 }

/// One sale's consumption of one lot. A sale that eats several lots produces
/// several rows, possibly mixing short- and long-term. Derived data: rebuilt
/// from raw transactions on every ticker rebuild, never edited in place.
public class LotConsumption
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public long SaleRecordId { get; set; }
    public long LotId { get; set; }
    public decimal SharesConsumed { get; set; }
    public decimal CostBasisConsumed { get; set; }
    public decimal Proceeds { get; set; }            // sale proceeds allocated pro-rata by shares
    public DateOnly LotAcquisitionDate { get; set; }
    public DateOnly SaleTradeDate { get; set; }
    public int HoldingDays { get; set; }
    public HoldingTerm Term { get; set; }            // ShortTerm when held one year or less
    public decimal GainLoss { get; set; }            // unadjusted; wash adjustments applied on read
}

/// A disallowed loss matched to a replacement lot. Stored as a delta so the
/// original lot and consumption rows stay untouched and everything can be
/// rebuilt from raw trades.
public class WashSaleAdjustment
{
    public long Id { get; set; }
    public long PositionId { get; set; }
    public long LotConsumptionId { get; set; }       // the loss consumption being washed
    public long ReplacementLotId { get; set; }       // root purchase lot absorbing the basis (any account)
    public decimal SharesMatched { get; set; }
    public decimal DisallowedLoss { get; set; }      // positive
    public WashKind Kind { get; set; }               // IraPermanent rows never adjust any basis
    public DateTimeOffset CreatedAt { get; set; }
}

/// Per-year capital loss carryforward. Computed rows are refreshed whenever tax
/// data changes; a manual row (user override to match the real return) wins and
/// is never overwritten.
public class TaxCarryforward
{
    public int Year { get; set; }                    // the year the loss arose (carried INTO Year+1)
    public decimal ShortTerm { get; set; }           // positive = loss carried forward
    public decimal LongTerm { get; set; }
    public bool IsManual { get; set; }
}
