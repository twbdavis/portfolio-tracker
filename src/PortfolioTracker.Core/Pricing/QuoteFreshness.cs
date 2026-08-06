using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

/// Detects a frozen upstream feed: the provider answers every request (so
/// connection tests pass) but keeps returning the same old observation.
/// Visible only as the gap between when we fetched and when the exchange
/// actually saw the price, and only meaningful while the market is open —
/// overnight and weekend fetches legitimately return the last session's print.
public static class QuoteFreshness
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// The newest exchange observation in the batch, or null when no quote
    /// carries one (provider predates the field, or column not yet backfilled).
    public static DateTimeOffset? NewestObservation(IEnumerable<PriceQuote> quotes)
    {
        DateTimeOffset? newest = null;
        foreach (var q in quotes)
            if (q.ObservedAt is DateTimeOffset ob && (newest is null || ob > newest)) newest = ob;
        return newest;
    }

    public static bool IsStale(DateTimeOffset? newestFetch, DateTimeOffset? newestObservation, DateTimeOffset utcNow)
        => newestFetch is DateTimeOffset f
           && newestObservation is DateTimeOffset o
           && MarketHours.IsOpen(utcNow)
           && f - o > StaleAfter;
}
