namespace PortfolioTracker.Core.Models;

/// One ticker within one account. The same ticker held in two accounts is two
/// Position rows; price targets live at ticker level in TickerTarget.
public class Position
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Ticker { get; set; } = "";
    public string? Cusip { get; set; }
    public string? Name { get; set; }
    public bool NeedsOpeningBalance { get; set; }
    public string? Notes { get; set; }
}
