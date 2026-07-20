namespace PortfolioTracker.Core.Models;

/// One price target per ticker regardless of how many accounts hold it —
/// quotes and alerts are per ticker, never per position row.
public class TickerTarget
{
    public long Id { get; set; }
    public string Ticker { get; set; } = "";
    public decimal? TargetPrice { get; set; }
    public TargetDirection TargetDirection { get; set; } = TargetDirection.AtOrAbove;
    public bool AlertEnabled { get; set; }
    public DateTimeOffset? AlertTriggeredAt { get; set; }
    public string? Notes { get; set; }
}
