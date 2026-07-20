namespace PortfolioTracker.Core.Models;

/// A researched candidate I might buy. Never included in portfolio totals.
public class WatchlistItem
{
    public long Id { get; set; }
    public string Ticker { get; set; } = "";
    public string? Name { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public string? Thesis { get; set; }
    public int Conviction { get; set; } = 3;          // 1–5
    public decimal? TargetBuyPrice { get; set; }
    public bool AlertEnabled { get; set; }
    public DateTimeOffset? AlertTriggeredAt { get; set; }
    public string? Notes { get; set; }
}
