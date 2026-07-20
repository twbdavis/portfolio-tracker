using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioTracker.Core;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Pricing;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Api.Services;

/// Rate-limit-aware background crawler that keeps the local metrics warehouse
/// fresh for every ticker in any universe. Design constraints, in order:
///
/// 1. NEVER starve interactive use — before every fetch it waits until the
///    InteractiveGate has been quiet for a cooldown, and it draws Finnhub
///    calls from BOTH its own budget gate (default 30/min, configurable) and
///    the shared 60/min gate, so interactive work always has headroom.
/// 2. Staleness-driven — the plan is recomputed from CrawlStamps each pass,
///    which is also what makes restart-resume free.
/// 3. Scheduled — runs after the 4:30 PM ET close on weekdays, on demand via
///    the API, and immediately on startup if a previous pass was interrupted.
///
/// History fetches (Yahoo Finance) don't touch Finnhub, so they bypass both
/// Finnhub gates and use a light politeness gate of their own.
public class UniverseCrawler(CoreServices svc, SignalService signals, ILogger<UniverseCrawler> log)
    : BackgroundService
{
    private static readonly TimeSpan InteractiveCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private const string IncompleteKey = "crawler.passIncomplete";
    private const string LastCompletedKey = "crawler.lastCompletedAt";

    private readonly SemaphoreSlim _runLock = new(1, 1);
    /// Politeness gate for the (non-Finnhub) history source.
    private readonly RateGate _historyGate = new(TimeSpan.FromSeconds(1));
    private readonly Queue<DateTimeOffset> _recentCalls = new();

    private volatile bool _running;
    private int _processed, _total;
    private string? _currentTicker;
    private string? _lastError;

    public record CrawlerStatus(bool Running, int Processed, int Total, string? CurrentTicker,
        DateTimeOffset? LastCompletedAt, string? LastError, int TotalTickers, int FreshTickers,
        int StaleTickers, int PendingItems, int BudgetPerMinute, int CallsLastMinute);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // let the shell finish booting and the startup quote pass go first
            await Task.Delay(TimeSpan.FromSeconds(15), ct);

            // an interrupted pass resumes regardless of the schedule window
            if (svc.AppState.Get(IncompleteKey) == "true")
                await RunPassAsync(null, ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (MarketHours.IsAfterClose(DateTimeOffset.UtcNow) && BuildPlan(null).Count > 0)
                        await RunPassAsync(null, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "crawl pass failed");
                    _lastError = ex.Message;
                }
                await Task.Delay(SweepInterval, ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    /// One full pass over the stale items; serves the scheduler and the
    /// "Refresh universe now" endpoint (optionally scoped to one universe).
    public async Task<bool> RunPassAsync(long? universeId, CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct)) return false;   // a pass is already running
        try
        {
            var plan = BuildPlan(universeId);
            _total = plan.Count;
            _processed = 0;
            _running = true;
            if (plan.Count == 0) return true;
            svc.AppState.Set(IncompleteKey, "true");

            // budget snapshot per pass; settings edits apply next pass
            var perMinute = Math.Clamp(svc.Settings.CrawlerCallsPerMinute, 1, 60);
            var crawlGate = new RateGate(TimeSpan.FromSeconds(60.0 / perMinute));

            var consecutiveHistoryFailures = 0;
            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();
                _currentTicker = item.Ticker;
                await WaitForInteractiveQuietAsync(ct);
                if (item.Kind == CrawlKind.History)
                {
                    await _historyGate.WaitAsync(ct);
                    // one bad ticker must not kill the pass: skip it unstamped
                    // (it re-plans next pass) — but a systemic outage should
                    // stop us hammering the source, hence the streak cap
                    try
                    {
                        await ProcessAsync(item, ct);
                        consecutiveHistoryFailures = 0;
                    }
                    catch (PriceFetchException ex)
                    {
                        consecutiveHistoryFailures++;
                        log.LogWarning(ex, "history fetch failed for {Ticker}", item.Ticker);
                        _lastError = $"{item.Ticker} history: {ex.Message}";
                        if (consecutiveHistoryFailures >= 25)
                            throw new PriceFetchException(
                                $"history source failing repeatedly; pausing until next pass ({ex.Message})", ex);
                    }
                }
                else
                {
                    await crawlGate.WaitAsync(ct);       // crawler budget
                    await svc.FinnhubGate.WaitAsync(ct); // shared free-tier budget
                    NoteCall();
                    await ProcessAsync(item, ct);
                }
                _processed++;
            }

            svc.AppState.Set(IncompleteKey, "false");
            svc.AppState.Set(LastCompletedKey, DateTimeOffset.Now.ToString("o"));
            _lastError = null;

            // nightly-after-crawl signal evaluation; its failure never breaks
            // the crawl itself
            try { await signals.EvaluateAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.LogWarning(ex, "signal evaluation after crawl failed"); }
            return true;
        }
        catch (PriceFetchException ex)
        {
            // missing key / rate limit / network: stop the pass, keep the
            // incomplete flag so the next sweep (or restart) resumes
            log.LogWarning(ex, "crawl pass aborted");
            _lastError = ex.Message;
            return true;
        }
        finally
        {
            _running = false;
            _currentTicker = null;
            _runLock.Release();
        }
    }

    private async Task ProcessAsync(CrawlWorkItem item, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        switch (item.Kind)
        {
            case CrawlKind.Profile:
                var profile = await svc.ResearchProvider.GetProfileAsync(item.Ticker, ct);
                if (profile is not null)
                    svc.Classifications.UpsertFromCrawl(item.Ticker, profile.Industry, null);
                break;

            case CrawlKind.Metrics:
                var m = await svc.ResearchProvider.GetMetricsAsync(item.Ticker, ct);
                if (m is not null)
                    svc.Metrics.Upsert(new TickerMetricsRow
                    {
                        Ticker = item.Ticker, AsOf = now,
                        MarketCap = m.MarketCap, Pe = m.PeTtm, Ps = m.Ps, Pb = m.Pb,
                        RevenueGrowthYoY = m.RevenueGrowthYoY, EpsGrowth = m.EpsGrowthYoY,
                        GrossMargin = m.GrossMargin, NetMargin = m.NetMargin, Roe = m.Roe,
                        Beta = m.Beta, Wk52High = m.High52, Wk52Low = m.Low52,
                    });
                break;

            case CrawlKind.Recommendations:
                var trends = await svc.ResearchProvider.GetRecommendationsAsync(item.Ticker, ct);
                svc.Recommendations.UpsertMany(item.Ticker, trends.Select(t => new RecommendationSnapshotRow
                {
                    Ticker = item.Ticker, Month = t.Period,
                    StrongBuy = t.StrongBuy, Buy = t.Buy, Hold = t.Hold,
                    Sell = t.Sell, StrongSell = t.StrongSell,
                }));
                break;

            case CrawlKind.Quote:
                var quote = await svc.PriceProvider.GetQuoteAsync(item.Ticker, ct);
                if (quote is not null)
                {
                    svc.PriceCache.Upsert(quote);
                    DailyCloseCapture.Capture(svc.DailyCloses, new[] { quote });
                }
                break;

            case CrawlKind.History:
                var from = CrawlPlanner.HistoryFetchStart(
                    svc.DailyCloses.CountForTicker(item.Ticker),
                    svc.DailyCloses.GetLatestDate(item.Ticker),
                    DateOnly.FromDateTime(DateTime.Today));
                var bars = await svc.History.GetDailyHistoryAsync(item.Ticker, from, ct);
                if (bars.Count > 0)
                    svc.DailyCloses.UpsertMany(item.Ticker, bars.Select(b => new DailyCloseRow
                    {
                        Ticker = item.Ticker, Date = b.Date, Close = b.Close, Source = CloseSource.Stooq,
                    }));
                break;
        }
        // null/empty results stamp too: an unknown symbol or Stooq coverage gap
        // must not be re-fetched until its TTL expires again
        svc.CrawlStamps.Stamp(item.Ticker, item.Kind, now);
    }

    private List<CrawlWorkItem> BuildPlan(long? universeId)
    {
        var tickers = universeId is long id
            ? svc.Universes.GetMembers(id)
            : svc.Universes.GetAllTickers();

        // held or watchlisted tickers get live quote polling already
        var openPositions = svc.Lots.GetAllOpen().GroupBy(l => l.PositionId)
            .Where(g => g.Sum(l => l.RemainingShares) != 0m)
            .Select(g => g.Key).ToHashSet();
        var live = svc.Positions.GetAll()
            .Where(p => openPositions.Contains(p.Id)).Select(p => p.Ticker)
            .Concat(svc.Watchlist.GetAll().Select(w => w.Ticker))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var userClassified = svc.Classifications.GetAll()
            .Where(c => c.Source == ClassificationSource.User)
            .Select(c => c.Ticker)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var latestCloses = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tickers)
            if (svc.DailyCloses.GetLatestDate(t) is DateOnly d)
                latestCloses[t] = d;

        var nowUtc = DateTimeOffset.UtcNow;
        return CrawlPlanner.Plan(tickers, svc.CrawlStamps.GetAll(), live, userClassified,
            latestCloses, MarketHours.MostRecentCompletedTradingDay(nowUtc), nowUtc);
    }

    private async Task WaitForInteractiveQuietAsync(CancellationToken ct)
    {
        while (!svc.Interactive.IsQuiet(InteractiveCooldown))
            await Task.Delay(250, ct);
    }

    private void NoteCall()
    {
        lock (_recentCalls)
        {
            var now = DateTimeOffset.UtcNow;
            _recentCalls.Enqueue(now);
            while (_recentCalls.Count > 0 && now - _recentCalls.Peek() > TimeSpan.FromMinutes(1))
                _recentCalls.Dequeue();
        }
    }

    public CrawlerStatus GetStatus()
    {
        var plan = BuildPlan(null);
        var totalTickers = svc.Universes.GetAllTickers().Count;
        var staleTickers = plan.Select(i => i.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int callsLastMinute;
        lock (_recentCalls)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
            callsLastMinute = _recentCalls.Count(t => t >= cutoff);
        }
        var lastCompleted = svc.AppState.Get(LastCompletedKey) is string s
            ? DateTimeOffset.Parse(s) : (DateTimeOffset?)null;
        return new CrawlerStatus(_running, _processed, _total, _currentTicker,
            lastCompleted, _lastError, totalTickers, totalTickers - staleTickers,
            staleTickers, plan.Count, Math.Clamp(svc.Settings.CrawlerCallsPerMinute, 1, 60),
            callsLastMinute);
    }
}
