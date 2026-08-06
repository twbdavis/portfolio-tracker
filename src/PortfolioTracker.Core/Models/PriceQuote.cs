namespace PortfolioTracker.Core.Models;

/// ObservedAt is when the exchange actually saw this price (Finnhub's `t`),
/// as opposed to FetchedAt, when we asked. A healthy feed keeps them close
/// during market hours; a frozen feed answers every fetch with the same old
/// observation — that gap is the only way to see the failure.
/// Source is null for the primary provider (Finnhub); the stale-feed fallback
/// stamps "yahoo" so the UI can say prices are flowing through the backup.
public record PriceQuote(string Ticker, decimal Price, DateTimeOffset FetchedAt,
    decimal? PrevClose = null, DateTimeOffset? ObservedAt = null, string? Source = null);
