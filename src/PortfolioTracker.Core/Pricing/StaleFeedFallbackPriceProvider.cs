using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

/// Self-healing wrapper for a primary feed that sometimes freezes (answers
/// every request with the same old observation — Finnhub, twice the week of
/// 2026-07-27). When the primary's quote is stale mid-session per
/// QuoteFreshness, ask the fallback and keep whichever observation is newer.
///
/// Deliberately narrow: hard failures from the primary (bad key, network,
/// 5xx) still propagate so they surface in the status bar instead of being
/// silently absorbed, and the fallback's own failures never mask a stale but
/// usable primary quote. Quotes keep their Source so the UI can disclose when
/// the backup is carrying the session.
public class StaleFeedFallbackPriceProvider(IPriceProvider primary, IPriceProvider fallback,
    Func<DateTimeOffset>? utcNow = null) : IPriceProvider
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public async Task<PriceQuote?> GetQuoteAsync(string ticker, CancellationToken ct)
    {
        var q = await primary.GetQuoteAsync(ticker, ct).ConfigureAwait(false);
        if (q is null) return null;   // unknown symbol: don't second-guess the primary
        if (!QuoteFreshness.IsStale(q.FetchedAt, q.ObservedAt, _utcNow())) return q;
        try
        {
            var fb = await fallback.GetQuoteAsync(ticker, ct).ConfigureAwait(false);
            if (fb?.ObservedAt is DateTimeOffset fo
                && (q.ObservedAt is not DateTimeOffset po || fo > po))
                return fb;
        }
        catch (PriceFetchException) { }   // fallback down too: stale data beats no data
        return q;
    }
}
