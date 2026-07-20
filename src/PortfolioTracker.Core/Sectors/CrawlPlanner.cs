namespace PortfolioTracker.Core.Sectors;

public enum CrawlKind { Profile = 0, Metrics = 1, Recommendations = 2, Quote = 3, History = 4 }

public record CrawlWorkItem(string Ticker, CrawlKind Kind);

/// Pure staleness planner: given the warehouse's freshness stamps, produce the
/// work items whose TTL has expired. No I/O, no clock reads — everything the
/// decision needs comes in as arguments, so TTL rules are exhaustively testable
/// and a plan is reproducible.
public static class CrawlPlanner
{
    public static readonly TimeSpan ProfileTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan MetricsTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan RecommendationsTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan QuoteTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan HistoryTtl = TimeSpan.FromHours(24);

    /// How far the one-time history backfill reaches: enough for SMA200 plus a
    /// year of headroom.
    public static readonly TimeSpan HistoryBackfill = TimeSpan.FromDays(730);

    /// A series shorter than this is a token (e.g. a single live-snapshot
    /// close), not a real backfill — fetch the full window, don't append.
    public const int MinBarsToAppend = 30;

    /// Where a history fetch should start. Appending from "latest + 1" is
    /// only valid once a real series exists; otherwise the one close captured
    /// from today's live quote would collapse the backfill into a
    /// starts-tomorrow request (empty at best, HTTP 400 at worst — the bug
    /// that shipped 2026-07-20). Always clamped to today.
    public static DateOnly HistoryFetchStart(int existingBars, DateOnly? latestClose, DateOnly today)
    {
        if (existingBars >= MinBarsToAppend && latestClose is DateOnly latest)
        {
            var next = latest.AddDays(1);
            return next > today ? today : next;
        }
        return today.AddDays(-(int)HistoryBackfill.TotalDays);
    }

    public static List<CrawlWorkItem> Plan(
        IReadOnlyCollection<string> universeTickers,
        IReadOnlyDictionary<(string Ticker, CrawlKind Kind), DateTimeOffset> stamps,
        IReadOnlySet<string> livePolledTickers,
        IReadOnlySet<string> userClassifiedTickers,
        IReadOnlyDictionary<string, DateOnly> latestCloseDates,
        DateOnly lastCompletedTradingDay,
        DateTimeOffset now)
    {
        var items = new List<CrawlWorkItem>();
        foreach (var ticker in universeTickers.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            bool Expired(CrawlKind kind, TimeSpan ttl) =>
                !stamps.TryGetValue((ticker, kind), out var at) || now - at >= ttl;

            // a user override is authoritative; re-crawling the profile would
            // only burn budget without being allowed to change anything
            if (!userClassifiedTickers.Contains(ticker) && Expired(CrawlKind.Profile, ProfileTtl))
                items.Add(new CrawlWorkItem(ticker, CrawlKind.Profile));

            if (Expired(CrawlKind.Metrics, MetricsTtl))
                items.Add(new CrawlWorkItem(ticker, CrawlKind.Metrics));

            if (Expired(CrawlKind.Recommendations, RecommendationsTtl))
                items.Add(new CrawlWorkItem(ticker, CrawlKind.Recommendations));

            // held and watchlisted tickers already get live quote polling
            if (!livePolledTickers.Contains(ticker) && Expired(CrawlKind.Quote, QuoteTtl))
                items.Add(new CrawlWorkItem(ticker, CrawlKind.Quote));

            // backfill once (no stamp), then daily appends only while the
            // series actually lags the last completed session
            var neverFetched = !stamps.ContainsKey((ticker, CrawlKind.History));
            var lagging = !latestCloseDates.TryGetValue(ticker, out var latest)
                          || latest < lastCompletedTradingDay;
            if (neverFetched || (Expired(CrawlKind.History, HistoryTtl) && lagging))
                items.Add(new CrawlWorkItem(ticker, CrawlKind.History));
        }
        return items;
    }
}
