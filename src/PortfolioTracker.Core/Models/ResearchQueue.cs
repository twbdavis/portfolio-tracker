namespace PortfolioTracker.Core.Models;

public enum DiscoveryStatus { New = 0, Accepted = 1, Ignored = 2 }

/// A ticker surfaced by peer expansion that isn't in any universe yet. The
/// first source ticker wins; an Ignored row never resurfaces (the crawler
/// checks existence, not status, before inserting).
public class DiscoveredTicker
{
    public string Ticker { get; set; } = "";
    public string SourceTicker { get; set; } = "";
    public DateTimeOffset DiscoveredAt { get; set; }
    public DiscoveryStatus Status { get; set; }
}

/// Nightly correlation of one candidate's daily returns against the current
/// portfolio composite. Correlation is null when fewer than the minimum
/// overlapping observations exist — never a fake 0.
public class PortfolioFitRow
{
    public string Ticker { get; set; } = "";
    public DateTimeOffset AsOf { get; set; }
    public decimal? Correlation { get; set; }
    public int Observations { get; set; }
}

/// Outcome tracking for one SignalHit. The baseline is snapshotted from the
/// warehouse (ticker close on the fire date, plus every sector member's close
/// as of that date, frozen as JSON so later membership changes can't rewrite
/// history). Forward returns fill in as each horizon matures.
public class SignalOutcomeRow
{
    public long SignalHitId { get; set; }
    public string Ticker { get; set; } = "";
    public string SignalKind { get; set; } = "";
    public string? Sector { get; set; }
    public DateOnly FireDate { get; set; }
    public decimal? TickerCloseAtFire { get; set; }
    /// {"MU":"123.45",...} — sector members' closes on the fire date.
    public string? SectorClosesJson { get; set; }
    public decimal? Return30 { get; set; }
    public decimal? Excess30 { get; set; }
    public decimal? Return60 { get; set; }
    public decimal? Excess60 { get; set; }
    public decimal? Return90 { get; set; }
    public decimal? Excess90 { get; set; }
}

/// One AI-generated research brief. InputSnapshotJson is the exact payload
/// the pipeline sliced per section — the grounding contract's audit trail.
public class ResearchBriefRow
{
    public long Id { get; set; }
    public string Ticker { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Model { get; set; } = "";
    public string InputSnapshotJson { get; set; } = "";
    public string BriefMarkdown { get; set; } = "";
    public int TokensUsed { get; set; }
    /// A section failed grounding checks (or the run was aborted); its text
    /// was replaced with a notice and the trace holds the evidence.
    public bool NeedsReview { get; set; }
    /// SHA-256 of InputSnapshotJson — re-requesting with unchanged data
    /// returns this brief instead of burning model calls.
    public string? InputSnapshotHash { get; set; }
}

/// One model interaction persisted for the "View generation trace" panel.
public class BriefTraceRow
{
    public long Id { get; set; }
    public long? BriefId { get; set; }
    public string Ticker { get; set; } = "";
    public string SnapshotHash { get; set; } = "";
    public string Section { get; set; } = "";
    public string Stage { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public string RawResponse { get; set; } = "";
    public string CleanedText { get; set; } = "";
    public string ViolationsJson { get; set; } = "";
    public bool Passed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
