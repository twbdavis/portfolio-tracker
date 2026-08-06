using System.Text.Json;
using Microsoft.Extensions.Logging;
using PortfolioTracker.Api.Endpoints;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Api.Services;

/// The nightly warehouse-derived jobs: portfolio-fit correlations and signal
/// outcome snapshots/maturation. Both are idempotent upserts recomputed from
/// warehouse state, so an interrupted run costs nothing — the next run
/// produces the same rows. Runs after each crawl pass and on demand.
public class ResearchJobsService(CoreServices svc, QueueService queue, BriefService briefs,
    ILogger<ResearchJobsService> log)
{
    public const string LastRunKey = "researchjobs.lastRunAt";
    public const string LastBriefBatchKey = "briefs.lastBatchAt";
    private readonly SemaphoreSlim _lock = new(1, 1);

    public record JobsResult(int FitRows, int OutcomeRows, int BatchBriefs);

    public async Task<JobsResult> RunAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var fitRows = ComputePortfolioFit();
            var outcomeRows = MaterializeOutcomes();
            var batch = await RunWeeklyBriefBatchAsync(ct);
            svc.AppState.Set(LastRunKey, DateTimeOffset.Now.ToString("o"));
            return new JobsResult(fitRows, outcomeRows, batch);
        }
        finally { _lock.Release(); }
    }

    /// Correlate every crawled ticker against the current-weight portfolio
    /// composite over the trailing 90 sessions.
    public int ComputePortfolioFit()
    {
        var weights = PortfolioFitMath.Weights(queue.Holdings());
        if (weights.Count == 0) return 0;   // nothing held: fit stays n/a everywhere

        // ~90 sessions plus weekend/holiday slack
        var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-200));
        var closes = svc.DailyCloses.GetAllByTicker(from);
        var composite = PortfolioFitMath.CompositeReturns(closes, weights);
        if (composite.Count == 0) return 0;

        var now = DateTimeOffset.Now;
        var rows = new List<PortfolioFitRow>();
        foreach (var ticker in svc.Universes.GetAllTickers())
        {
            var candidateCloses = closes.GetValueOrDefault(ticker) ?? new List<DailyCloseRow>();
            var (corr, obs) = PortfolioFitMath.Correlate(candidateCloses, composite);
            rows.Add(new PortfolioFitRow { Ticker = ticker, AsOf = now, Correlation = corr, Observations = obs });
        }
        svc.PortfolioFit.UpsertMany(rows);
        log.LogInformation("portfolio fit: {Count} tickers correlated", rows.Count);
        return rows.Count;
    }

    /// Snapshot fire-time baselines for new hits and fill forward returns for
    /// horizons that have matured since the last run.
    public int MaterializeOutcomes()
    {
        var hits = svc.SignalHits.GetAll(includeDismissed: true)
            .Where(h => h.SignalKind != SignalKinds.BreadthThrust)   // sector-level: no ticker close
            .ToList();
        if (hits.Count == 0) return 0;

        var existing = svc.SignalOutcomes.GetAll().ToDictionary(o => o.SignalHitId);
        var oldestFire = hits.Min(h => DateOnly.FromDateTime(h.FiredAt.LocalDateTime));
        var closes = svc.DailyCloses.GetAllByTicker(oldestFire.AddDays(-10));
        var groups = SectorEndpoints.BuildGroups(svc);

        var changed = 0;
        foreach (var hit in hits)
        {
            var outcome = existing.GetValueOrDefault(hit.Id);
            if (outcome is null)
            {
                outcome = Snapshot(hit, closes, groups);
                if (outcome is null) continue;   // no baseline close yet; retry next run
                Mature(outcome, closes);
                svc.SignalOutcomes.Upsert(outcome);
                changed++;
                continue;
            }
            if (Mature(outcome, closes))
            {
                svc.SignalOutcomes.Upsert(outcome);
                changed++;
            }
        }
        if (changed > 0) log.LogInformation("signal outcomes: {Count} rows updated", changed);
        return changed;
    }

    private SignalOutcomeRow? Snapshot(SignalHit hit,
        Dictionary<string, List<DailyCloseRow>> closes,
        Dictionary<string, List<TickerAnalysis>> groups)
    {
        var fireDate = DateOnly.FromDateTime(hit.FiredAt.LocalDateTime);
        var tickerCloses = closes.GetValueOrDefault(hit.Ticker) ?? new List<DailyCloseRow>();
        var baseline = OutcomeMath.CloseOnOrBefore(tickerCloses, fireDate);
        if (baseline is null) return null;

        // freeze the sector membership and their fire-date closes now, so a
        // later universe edit can't quietly rewrite the benchmark
        string? sector = null;
        var memberCloses = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var (groupSector, rows) in groups)
        {
            if (groupSector == SectorAnalyzer.Unclassified) continue;
            if (!rows.Any(t => string.Equals(t.Ticker, hit.Ticker, StringComparison.OrdinalIgnoreCase)))
                continue;
            sector = groupSector;
            foreach (var member in rows)
                if (OutcomeMath.CloseOnOrBefore(
                        closes.GetValueOrDefault(member.Ticker) ?? new List<DailyCloseRow>(),
                        fireDate) is decimal c)
                    memberCloses[member.Ticker] = c;
            break;
        }

        return new SignalOutcomeRow
        {
            SignalHitId = hit.Id,
            Ticker = hit.Ticker,
            SignalKind = hit.SignalKind,
            Sector = sector,
            FireDate = fireDate,
            TickerCloseAtFire = baseline,
            SectorClosesJson = memberCloses.Count > 0
                ? JsonSerializer.Serialize(memberCloses) : null,
        };
    }

    /// Fills any horizon that is still null but has matured. Returns whether
    /// anything changed.
    private static bool Mature(SignalOutcomeRow outcome, Dictionary<string, List<DailyCloseRow>> closes)
    {
        if (outcome.TickerCloseAtFire is not decimal baseline || baseline <= 0m) return false;
        var tickerCloses = closes.GetValueOrDefault(outcome.Ticker) ?? new List<DailyCloseRow>();
        Dictionary<string, decimal>? members = null;
        if (outcome.SectorClosesJson is string json)
        {
            try { members = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json); }
            catch (JsonException) { /* unreadable snapshot: ticker returns still fill */ }
        }

        var changed = false;
        foreach (var horizon in OutcomeMath.Horizons)
        {
            if (GetReturn(outcome, horizon) is not null) continue;
            var ret = OutcomeMath.ForwardReturn(tickerCloses, outcome.FireDate, baseline, horizon);
            if (ret is null) continue;
            SetReturn(outcome, horizon, ret);
            if (members is not null
                && OutcomeMath.SectorMedianForwardReturn(members, closes, outcome.FireDate, horizon)
                    is decimal sectorMedian)
                SetExcess(outcome, horizon, ret - sectorMedian);
            changed = true;
        }
        return changed;
    }

    /// Weekly batch briefs for the top queue candidates — only when enabled,
    /// keyed, and at least 7 days since the last batch.
    private async Task<int> RunWeeklyBriefBatchAsync(CancellationToken ct)
    {
        if (!svc.Settings.AiWeeklyBatchEnabled || !briefs.Usage().Enabled)
            return 0;
        if (svc.AppState.Get(LastBriefBatchKey) is string s
            && DateTimeOffset.TryParse(s, out var last)
            && DateTimeOffset.Now - last < TimeSpan.FromDays(7))
            return 0;

        var count = Math.Clamp(svc.Settings.AiWeeklyBatchCount, 1, 20);
        var built = queue.Build(includeHeld: false);
        var recent = svc.Briefs.GetLatestIdByTicker();
        var generated = 0;
        foreach (var candidate in built.Candidates)
        {
            if (generated >= count) break;
            ct.ThrowIfCancellationRequested();
            // one brief per ticker per batch cycle; manual requests can always add more
            if (recent.ContainsKey(candidate.Ticker)
                && svc.Briefs.GetForTicker(candidate.Ticker).FirstOrDefault() is { } newest
                && DateTimeOffset.Now - newest.CreatedAt < TimeSpan.FromDays(7))
                continue;
            try
            {
                await briefs.GenerateAsync(candidate.Ticker, ct);
                generated++;
            }
            catch (BriefGenerationException ex)
            {
                log.LogWarning(ex, "weekly brief for {Ticker} failed; stopping batch", candidate.Ticker);
                break;   // budget exhausted or API down: don't hammer
            }
        }
        svc.AppState.Set(LastBriefBatchKey, DateTimeOffset.Now.ToString("o"));
        return generated;
    }

    private static decimal? GetReturn(SignalOutcomeRow o, int h) =>
        h switch { 30 => o.Return30, 60 => o.Return60, 90 => o.Return90, _ => null };

    private static void SetReturn(SignalOutcomeRow o, int h, decimal? v)
    {
        if (h == 30) o.Return30 = v;
        else if (h == 60) o.Return60 = v;
        else if (h == 90) o.Return90 = v;
    }

    private static void SetExcess(SignalOutcomeRow o, int h, decimal? v)
    {
        if (h == 30) o.Excess30 = v;
        else if (h == 60) o.Excess60 = v;
        else if (h == 90) o.Excess90 = v;
    }
}
