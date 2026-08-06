using System.Text.Json;
using PortfolioTracker.Api.Endpoints;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Api.Services;

/// Assembles warehouse state into QueueEngine inputs. Read paths only — the
/// queue must render from local data with zero live API calls, so everything
/// here is repository reads plus pure functions.
public class QueueService(CoreServices svc)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public record BuiltQueue(
        List<QueueCandidate> Candidates,
        HashSet<string> HeldTickers,
        Dictionary<string, DateTimeOffset> Snoozed,
        List<(string Sector, decimal WeightPct)> PortfolioSectorWeights,
        Dictionary<string, PortfolioFitRow> FitByTicker,
        Dictionary<string, string> SectorByTicker,
        QueueSettings Settings);

    public QueueSettings LoadSettings() =>
        QueueSettings.Load(Path.Combine(svc.AppDataDir, "queue.json"));

    /// Current holdings as (ticker, shares, price) — shares from open lots,
    /// price from the quote cache.
    public List<(string Ticker, decimal Shares, decimal Price)> Holdings()
    {
        var sharesByPosition = svc.Lots.GetAllOpen()
            .GroupBy(l => l.PositionId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.RemainingShares));
        var prices = svc.PriceCache.GetAll();
        var holdings = new List<(string, decimal, decimal)>();
        foreach (var p in svc.Positions.GetAll())
        {
            if (!sharesByPosition.TryGetValue(p.Id, out var shares) || shares <= 0m) continue;
            var price = prices.TryGetValue(p.Ticker, out var q) ? q.Price : 0m;
            holdings.Add((p.Ticker.ToUpperInvariant(), shares, price));
        }
        return holdings;
    }

    public HashSet<string> HeldTickers() =>
        Holdings().Select(h => h.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// Signal kinds the queue should down-weight (empty unless the setting is
    /// on) — derived from the scoreboard, never hand-maintained.
    public Dictionary<string, string> DownWeightedKinds()
    {
        if (!svc.Settings.ScoreboardDownWeighting)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = ScoreboardMath.Build(svc.SignalHits.GetAll(includeDismissed: true),
            svc.SignalOutcomes.GetAll(), downWeightingEnabled: true);
        return ScoreboardMath.DownWeightedKinds(rows, enabled: true);
    }

    public BuiltQueue Build(bool includeHeld)
    {
        var settings = LoadSettings();
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(DateTime.Today);

        var groups = SectorEndpoints.BuildGroups(svc);
        var held = HeldTickers();
        var snoozed = svc.QueueSnoozes.GetActive(now);

        // pool: every crawled ticker (universes already include accepted
        // discoveries); snoozed always out, held out unless toggled in
        var pool = svc.Universes.GetAllTickers()
            .Where(t => !snoozed.ContainsKey(t))
            .Where(t => includeHeld || !held.Contains(t))
            .ToList();

        // active = undismissed; each kind counts once (QueueEngine dedupes too)
        var signalKinds = svc.SignalHits.GetAll(includeDismissed: false)
            .Where(h => h.SignalKind != SignalKinds.BreadthThrust)   // sector-level, not a ticker
            .GroupBy(h => h.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                g => g.Select(h => h.SignalKind).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var quadrants = new Dictionary<string, SectorQuadrant?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sector, rows) in groups)
        {
            if (sector == SectorAnalyzer.Unclassified) continue;
            var summary = SectorAnalyzer.Summarize(sector, rows);
            quadrants[sector] = SectorHealth.Classify(summary.BreadthAboveSma50, summary.MedianReturn20d);
        }

        var inputs = new QueueInputs(pool, groups, signalKinds, quadrants,
            EarningsFromCache(groups), svc.PortfolioFit.GetAll(), DownWeightedKinds());
        var candidates = QueueEngine.Rank(inputs, settings, today);

        var weights = PortfolioFitMath.Weights(Holdings());
        var sectorByTicker = svc.Classifications.GetAll()
            .Where(c => !string.IsNullOrWhiteSpace(c.Sector))
            .ToDictionary(c => c.Ticker, c => c.Sector!, StringComparer.OrdinalIgnoreCase);

        return new BuiltQueue(candidates, held, snoozed,
            PortfolioFitMath.SectorWeights(weights, sectorByTicker),
            svc.PortfolioFit.GetAll(), sectorByTicker, settings);
    }

    /// The fit sentence for one candidate, with the portfolio's sector weights
    /// already in hand (avoids rebuilding them per row).
    public string FitLine(string ticker, string? sector,
        IReadOnlyList<(string Sector, decimal WeightPct)> portfolioSectorWeights,
        Dictionary<string, PortfolioFitRow> fitByTicker, QueueSettings settings)
    {
        var fit = fitByTicker.GetValueOrDefault(ticker);
        var sectorWeight = sector is null ? 0m
            : portfolioSectorWeights.FirstOrDefault(w =>
                string.Equals(w.Sector, sector, StringComparison.OrdinalIgnoreCase)).WeightPct;
        return PortfolioFitMath.FitLine(fit?.Correlation, fit?.Observations ?? 0,
            sector, sectorWeight, settings.SectorOverlapPct);
    }

    /// Earnings from the local research cache only — same sourcing rule as the
    /// signals engine: a queue build never triggers a live call.
    private Dictionary<string, EarningsInfo> EarningsFromCache(
        Dictionary<string, List<TickerAnalysis>> groups)
    {
        var ttl = TimeSpan.FromDays(7);
        var earnings = new Dictionary<string, EarningsInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticker in groups.Values.SelectMany(g => g).Select(t => t.Ticker))
        {
            var quarters = CacheGet<List<EarningsQuarter>>(ticker, "earnings", ttl);
            var next = CacheGet<DateOnly?>(ticker, "earningsdate", ttl);
            if (quarters is { Count: > 0 })
                earnings[ticker] = new EarningsInfo(quarters, next);
        }
        return earnings;
    }

    private T? CacheGet<T>(string ticker, string kind, TimeSpan ttl)
    {
        var json = svc.ResearchCache.Get(ticker, kind, ttl);
        if (json is null) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch (JsonException) { return default; }
    }
}
