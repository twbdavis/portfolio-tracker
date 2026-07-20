using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioTracker.Api.Hubs;
using PortfolioTracker.Core;
using PortfolioTracker.Core.Alerts;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Api.Services;

/// The quote polling loop from the WinForms era, now pushing over SignalR:
/// polls once at startup regardless of market hours, then only while the
/// market is open, at the settings interval. Evaluates ticker targets and
/// watchlist buy alerts on every batch.
public class QuotePoller(CoreServices svc, IHubContext<PortfolioHub> hub, AlertEvents alerts,
    ILogger<QuotePoller> log) : BackgroundService
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    public string? LastError { get; private set; }
    public List<string> LastUnresolved { get; private set; } = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await RefreshNowAsync(ct); }   // spec: outside market hours, poll once on launch
        catch (Exception ex) { log.LogWarning(ex, "startup quote refresh failed"); }
        while (!ct.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(10, svc.Settings.RefreshIntervalSeconds));
            try
            {
                await Task.Delay(interval, ct);
                if (MarketHours.IsOpen(DateTimeOffset.UtcNow))
                    await RefreshNowAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogWarning(ex, "quote refresh failed"); }
        }
    }

    /// One refresh pass; also serves the manual "Refresh Prices" endpoint.
    public async Task<PriceRefreshResult?> RefreshNowAsync(CancellationToken ct)
    {
        if (!await _refreshLock.WaitAsync(0, ct)) return null;   // a refresh is already running
        using var _ = svc.Interactive.Begin();   // quote passes preempt the universe crawler
        try
        {
            // per ticker, never per row/tab: shares held anywhere, one target per
            // ticker, watchlist buy targets — one distinct list, one quote each
            var openByPosition = svc.Lots.GetAllOpen()
                .GroupBy(l => l.PositionId)
                .Where(g => g.Sum(l => l.RemainingShares) != 0m)
                .Select(g => g.Key).ToHashSet();
            var tickers = svc.Positions.GetAll()
                .Where(p => openByPosition.Contains(p.Id))
                .Select(p => p.Ticker)
                .Concat(svc.TickerTargets.GetAll()
                    .Where(t => t.TargetPrice is not null)
                    .Select(t => t.Ticker))
                .Concat(svc.Watchlist.GetAll()
                    .Where(w => w.AlertEnabled && w.TargetBuyPrice is not null)
                    .Select(w => w.Ticker))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tickers.Count == 0) return new PriceRefreshResult(new(), new(), null);

            // each completed wave streams to the UI immediately; alerts wait
            // for the full pass so a target is evaluated at most once per pass
            var result = await svc.Prices.RefreshAsync(tickers, ct, batch =>
            {
                var push = batch.Select(q => new QuotePush(q.Ticker, q.Price, q.PrevClose, q.FetchedAt)).ToList();
                return hub.Clients.All.SendAsync("quotes", push, ct);
            });
            LastError = result.Error;
            LastUnresolved = result.Unresolved;
            DailyCloseCapture.Capture(svc.DailyCloses, result.Quotes.Values);
            await CheckAlertsAsync(result.Quotes, ct);
            return result;
        }
        finally { _refreshLock.Release(); }
    }

    private async Task CheckAlertsAsync(Dictionary<string, PriceQuote> quotes, CancellationToken ct)
    {
        // targets are ticker-level: one evaluation per ticker regardless of accounts
        foreach (var target in svc.TickerTargets.GetAll())
        {
            if (!quotes.TryGetValue(target.Ticker, out var q)) continue;
            if (!AlertEvaluator.ShouldFire(target, q.Price)) continue;
            svc.TickerTargets.StampAlert(target.Ticker, DateTimeOffset.Now);
            var direction = target.TargetDirection == TargetDirection.AtOrAbove ? "target" : "buy target";
            await FireAsync(new AlertPush("position", target.Ticker, q.Price, target.TargetPrice!.Value,
                $"{target.Ticker} hit {q.Price:C2} - {direction} {target.TargetPrice:C2}"), ct);
        }
        foreach (var w in svc.Watchlist.GetAll())
        {
            if (!quotes.TryGetValue(w.Ticker, out var q)) continue;
            if (!AlertEvaluator.ShouldFire(w, q.Price)) continue;
            svc.Watchlist.StampAlert(w.Id, DateTimeOffset.Now);
            await FireAsync(new AlertPush("watchlist", w.Ticker, q.Price, w.TargetBuyPrice!.Value,
                $"{w.Ticker} dropped to {q.Price:C2} - your buy target was {w.TargetBuyPrice:C2}"), ct);
        }
    }

    private async Task FireAsync(AlertPush alert, CancellationToken ct)
    {
        await hub.Clients.All.SendAsync("alertFired", alert, ct);
        alerts.Raise(alert);   // shell toast
    }
}
