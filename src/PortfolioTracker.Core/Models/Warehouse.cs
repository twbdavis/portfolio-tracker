namespace PortfolioTracker.Core.Models;

/// A bounded ticker list that the crawler keeps warehoused. Seeded universes
/// ship with the app but stay fully editable afterwards.
public class Universe
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsSeeded { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum ClassificationSource { Finnhub = 0, User = 1 }

/// Sector/industry per ticker. Sector is the grouping key for all sector
/// analysis; a User row is never overwritten by crawled data.
public class TickerClassification
{
    public string Ticker { get; set; } = "";
    public string? Sector { get; set; }
    public string? Industry { get; set; }
    public ClassificationSource Source { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// Latest warehoused fundamentals snapshot for one ticker. Nullable throughout:
/// ETFs legitimately lack most of these.
public class TickerMetricsRow
{
    public string Ticker { get; set; } = "";
    public DateTimeOffset AsOf { get; set; }
    public decimal? MarketCap { get; set; }
    public decimal? Pe { get; set; }
    public decimal? Ps { get; set; }
    public decimal? Pb { get; set; }
    public decimal? RevenueGrowthYoY { get; set; }
    public decimal? EpsGrowth { get; set; }
    public decimal? GrossMargin { get; set; }
    public decimal? NetMargin { get; set; }
    public decimal? Roe { get; set; }
    public decimal? Beta { get; set; }
    public decimal? Wk52High { get; set; }
    public decimal? Wk52Low { get; set; }
}

/// One month of analyst recommendation counts, warehoused so RecMomentum can
/// compare consecutive months without re-fetching.
public class RecommendationSnapshotRow
{
    public string Ticker { get; set; } = "";
    public string Month { get; set; } = "";   // "2026-06-01", finnhub period format
    public int StrongBuy { get; set; }
    public int Buy { get; set; }
    public int Hold { get; set; }
    public int Sell { get; set; }
    public int StrongSell { get; set; }

    public int Total => StrongBuy + Buy + Hold + Sell + StrongSell;
    public decimal? BuyRatio => Total == 0 ? null : (decimal)(StrongBuy + Buy) / Total;
}

public enum CloseSource { Stooq = 0, LiveSnapshot = 1 }

public class DailyCloseRow
{
    public string Ticker { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal Close { get; set; }
    public CloseSource Source { get; set; }
}

/// One firing of an opportunity signal. InputsJson stores the exact observed
/// values and thresholds so the "why this fired" panel is reproducible.
public class SignalHit
{
    public long Id { get; set; }
    public string Ticker { get; set; } = "";
    public string SignalKind { get; set; } = "";
    public DateTimeOffset FiredAt { get; set; }
    public string InputsJson { get; set; } = "";
    public bool Dismissed { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
}
