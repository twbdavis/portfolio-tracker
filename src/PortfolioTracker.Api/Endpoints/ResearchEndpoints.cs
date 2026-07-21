using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Pricing;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Api.Endpoints;

/// The ResearchForm's panels over HTTP, one endpoint per panel so the frontend
/// streams them in as the shared Finnhub rate gate lets each call through —
/// the aggregate all-panels response made every open wait for the slowest
/// panel. Failures carry an error string per panel; a missing key never 500s.
/// Cancellation matters here: an abandoned panel request (user picked another
/// ticker) frees its queued rate-gate slot for the next search.
public static class ResearchEndpoints
{
    public record SearchResponse(List<SymbolSearchResult> Results, string? Error);

    public static void MapResearchEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/research/search", async (string? q, CoreServices svc, CancellationToken ct) =>
        {
            q = q?.Trim() ?? "";
            if (q.Length == 0) return Results.Ok(new SearchResponse(new(), null));
            using var _ = svc.Interactive.Begin();   // preempts the universe crawler
            try { return Results.Ok(new SearchResponse(await svc.Research.SearchAsync(q, ct), null)); }
            catch (PriceFetchException ex) { return Results.Ok(new SearchResponse(new(), ex.Message)); }
        });

        MapPanel(api, "snapshot", async (svc, ticker, ct) =>
        {
            var quote = await svc.Research.GetQuoteDetailAsync(ticker, ct);
            var profile = await svc.Research.GetProfileAsync(ticker, ct);
            return new { quote, profile };
        });

        MapPanel(api, "fundamentals", async (svc, ticker, ct) =>
            new { metrics = await svc.Research.GetMetricsAsync(ticker, ct) });

        MapPanel(api, "recommendations", async (svc, ticker, ct) =>
            new { trends = await svc.Research.GetRecommendationsAsync(ticker, ct) });

        MapPanel(api, "earnings", async (svc, ticker, ct) =>
        {
            var quarters = await svc.Research.GetEarningsAsync(ticker, ct);
            var next = await svc.Research.GetNextEarningsDateAsync(ticker, ct);
            return new { quarters, nextEarningsDate = next };
        });

        MapPanel(api, "news", async (svc, ticker, ct) =>
            new { items = await svc.Research.GetNewsAsync(ticker, ct) });

        // all-time daily closes for the snapshot price chart. Yahoo (keyless),
        // so it skips the Finnhub rate gate and isn't a MapPanel; cached an
        // hour — closes only move once per trading day. When Yahoo is down the
        // warehouse's crawled closes (~2y for universe tickers) keep the chart
        // alive, with the fetch error surfaced alongside the stale bars.
        api.MapGet("/research/{ticker}/history", async (string ticker, CoreServices svc, CancellationToken ct) =>
        {
            ticker = ticker.Trim().ToUpperInvariant();
            if (ticker.Length == 0) return Results.BadRequest(new { error = "ticker is required" });
            var cached = svc.ResearchCache.Get(ticker, "history", TimeSpan.FromHours(1));
            if (cached is not null)
                return Results.Ok(new { value = JsonSerializer.Deserialize<HistoryValue>(cached), error = (string?)null });
            using var _ = svc.Interactive.Begin();   // the crawler shares Yahoo; don't compete with it
            try
            {
                var bars = await svc.History.GetDailyHistoryAsync(ticker, new DateOnly(1970, 1, 1), ct);
                var value = new HistoryValue(bars);
                svc.ResearchCache.Put(ticker, "history", JsonSerializer.Serialize(value), DateTimeOffset.Now);
                return Results.Ok(new { value, error = (string?)null });
            }
            catch (PriceFetchException ex)
            {
                var fallback = svc.DailyCloses.GetForTicker(ticker)
                    .Select(r => new DailyBar(r.Date, r.Close)).ToList();
                return Results.Ok(new
                {
                    value = fallback.Count > 0 ? new HistoryValue(fallback) : null,
                    error = (string?)ex.Message,
                });
            }
        });

        // warehouse-only sector context — never a live API call, so it isn't
        // a MapPanel (no rate gate, no interactive preemption) and it renders
        // instantly even when Finnhub is down
        api.MapGet("/research/{ticker}/sector-context", (string ticker, CoreServices svc) =>
        {
            ticker = ticker.Trim().ToUpperInvariant();
            var groups = SectorEndpoints.BuildGroups(svc);
            foreach (var (sector, rows) in groups)
            {
                if (sector == SectorAnalyzer.Unclassified) continue;
                var mine = rows.FirstOrDefault(t =>
                    string.Equals(t.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
                if (mine is null) continue;
                return Results.Ok(BuildSectorContext(sector, mine, rows));
            }
            return Results.Ok(new SectorContextResponse(null, 0, null, new()));
        });
    }

    public record HistoryValue(List<DailyBar> Bars);

    public record SectorMetricContext(string Key, string Label, decimal? Value,
        decimal? SectorMedian, decimal? Percentile, bool IsPercent);

    public record SectorContextResponse(string? Sector, int CoverageCount,
        DateTimeOffset? AsOf, List<SectorMetricContext> Metrics);

    private static SectorContextResponse BuildSectorContext(string sector, TickerAnalysis mine,
        List<TickerAnalysis> rows)
    {
        var metrics = new List<SectorMetricContext>();
        void Add(string key, string label, Func<TickerAnalysis, decimal?> pick, bool isPercent)
        {
            var pop = rows.Select(pick).Where(v => v is not null).Select(v => v!.Value).ToList();
            var value = pick(mine);
            metrics.Add(new SectorMetricContext(key, label, value,
                RobustStats.Median(pop),
                value is decimal v ? RobustStats.Percentile(v, pop) : null,
                isPercent));
        }

        Add("pe", "P/E (TTM)", t => t.Metrics?.Pe, false);
        Add("ps", "P/S", t => t.Metrics?.Ps, false);
        Add("pb", "P/B", t => t.Metrics?.Pb, false);
        Add("revenueGrowthYoY", "Revenue growth YoY", t => t.Metrics?.RevenueGrowthYoY, true);
        Add("grossMargin", "Gross margin", t => t.Metrics?.GrossMargin, true);
        Add("netMargin", "Net margin", t => t.Metrics?.NetMargin, true);
        Add("roe", "ROE", t => t.Metrics?.Roe, true);
        Add("pctOff52wHigh", "% off 52w high", t => t.Derived.PctOff52wHigh, true);
        Add("return20d", "20d return", t => t.Derived.Return20d, true);

        return new SectorContextResponse(sector, rows.Count, mine.Metrics?.AsOf, metrics);
    }

    /// GET /research/{ticker}/{panel} → the panel payload plus an `error`
    /// field; provider/auth failures fill `error` and null the payload.
    private static void MapPanel<T>(IEndpointRouteBuilder api, string panel,
        Func<CoreServices, string, CancellationToken, Task<T>> load)
    {
        api.MapGet($"/research/{{ticker}}/{panel}", async (string ticker, CoreServices svc, CancellationToken ct) =>
        {
            ticker = ticker.Trim().ToUpperInvariant();
            if (ticker.Length == 0) return Results.BadRequest(new { error = "ticker is required" });
            using var _ = svc.Interactive.Begin();   // preempts the universe crawler
            try
            {
                var value = await load(svc, ticker, ct);
                return Results.Ok(new { value, error = (string?)null });
            }
            catch (PriceFetchException ex)
            {
                return Results.Ok(new { value = (T?)default, error = (string?)ex.Message });
            }
        });
    }
}
