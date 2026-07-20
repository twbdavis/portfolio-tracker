using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using PortfolioTracker.Api.Endpoints;
using PortfolioTracker.Api.Hubs;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Api.Services;

/// Runs the pure SignalEngine over the warehouse, applies the 14-day dedupe,
/// persists hits, and (only for kinds the user explicitly opted into) routes
/// them to the toast pipeline. Ships with every kind OFF.
public class SignalService(CoreServices svc, IHubContext<PortfolioHub> hub, AlertEvents alerts,
    ILogger<SignalService> log)
{
    public const string LastEvaluatedKey = "signals.lastEvaluatedAt";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// Evaluate all universes against the warehouse; returns how many new
    /// hits fired. Deterministic given warehouse state and signals.json.
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var settings = SignalSettings.Load(Path.Combine(AppDataDir(), "signals.json"));
            var inputs = BuildInputs();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var candidates = SignalEngine.Evaluate(inputs, settings, today);

            var now = DateTimeOffset.Now;
            var fired = 0;
            foreach (var c in candidates)
            {
                var prior = svc.SignalHits.GetLatest(c.Ticker, c.Kind);
                // spec: no re-fire while a prior hit is < DedupeDays old and undismissed
                if (prior is not null && !prior.Dismissed
                    && now - prior.FiredAt < TimeSpan.FromDays(settings.DedupeDays))
                    continue;

                var hit = new SignalHit
                {
                    Ticker = c.Ticker,
                    SignalKind = c.Kind,
                    FiredAt = now,
                    InputsJson = JsonSerializer.Serialize(c.Why, JsonOpts),
                };
                hit.Id = svc.SignalHits.Insert(hit);
                fired++;

                // opt-in toasts only; the feed is the default surface
                if (svc.Settings.SignalToastKinds.Contains(c.Kind, StringComparer.OrdinalIgnoreCase))
                {
                    var push = new AlertPush("signal", c.Ticker, 0m, 0m, c.Why.Summary);
                    await hub.Clients.All.SendAsync("alertFired", push, ct);
                    alerts.Raise(push);
                }
            }

            svc.AppState.Set(LastEvaluatedKey, now.ToString("o"));
            if (fired > 0) log.LogInformation("signals: {Count} new hits", fired);
            return fired;
        }
        finally { _lock.Release(); }
    }

    /// Warehouse snapshot for the engine. Earnings come from the local
    /// research cache (populated when tickers are researched) — CatalystWatch
    /// quietly skips tickers without cached earnings rather than calling out.
    private SignalInputs BuildInputs()
    {
        var groups = SectorEndpoints.BuildGroups(svc);
        var closes = svc.DailyCloses.GetAllByTicker(
            DateOnly.FromDateTime(DateTime.Today.AddDays(-420)));
        var recs = svc.Recommendations.GetAllByTicker();

        // earnings dates drift rarely; a week of staleness beats no coverage
        var earningsTtl = TimeSpan.FromDays(7);
        var earnings = new Dictionary<string, EarningsInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticker in groups.Values.SelectMany(g => g).Select(t => t.Ticker))
        {
            var quarters = CacheGet<List<EarningsQuarter>>(ticker, "earnings", earningsTtl);
            var next = CacheGet<DateOnly?>(ticker, "earningsdate", earningsTtl);
            if (quarters is { Count: > 0 })
                earnings[ticker] = new EarningsInfo(quarters, next);
        }
        return new SignalInputs(groups, recs, closes, earnings);
    }

    private T? CacheGet<T>(string ticker, string kind, TimeSpan ttl)
    {
        var json = svc.ResearchCache.Get(ticker, kind, ttl);
        if (json is null) return default;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return default; }
    }

    private string AppDataDir() => svc.AppDataDir;
}
