using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Api.Endpoints;

/// Sector analysis endpoints. Warehouse-only by design: every handler reads
/// SQLite and computes in memory — an analysis query must never trigger a
/// live API call (that's the crawler's job).
public static class SectorEndpoints
{
    /// Calendar days of closes loaded for derived metrics: enough trading
    /// days for SMA200 + cross lookback and the 52w-high fallback.
    private const int CloseWindowDays = 420;

    public static Dictionary<string, List<TickerAnalysis>> BuildGroups(CoreServices svc)
    {
        var tickers = svc.Universes.GetAllTickers();
        var closes = svc.DailyCloses.GetAllByTicker(
            DateOnly.FromDateTime(DateTime.Today.AddDays(-CloseWindowDays)));
        return SectorAnalyzer.GroupBySector(tickers, svc.Classifications.GetAll(),
            svc.Metrics.GetAll(), closes);
    }

    public static void MapSectorEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/sectors", (CoreServices svc) =>
            Results.Ok(SectorAnalyzer.Sectors(BuildGroups(svc))));

        api.MapGet("/sectors/heatmap", (CoreServices svc) =>
            Results.Ok(SectorAnalyzer.Heatmap(BuildGroups(svc))));

        api.MapGet("/sectors/{sector}/summary", (string sector, CoreServices svc) =>
            FindGroup(svc, sector) is { } g
                ? Results.Ok(SectorAnalyzer.Summarize(g.Key, g.Value))
                : Results.NotFound(new { error = $"no crawled tickers in sector \"{sector}\"" }));

        api.MapGet("/sectors/{sector}/rankings", (string sector, CoreServices svc) =>
            FindGroup(svc, sector) is { } g
                ? Results.Ok(SectorAnalyzer.Rankings(g.Value))
                : Results.NotFound(new { error = $"no crawled tickers in sector \"{sector}\"" }));
    }

    private static KeyValuePair<string, List<TickerAnalysis>>? FindGroup(CoreServices svc, string sector)
    {
        var groups = BuildGroups(svc);
        foreach (var g in groups)
            if (string.Equals(g.Key, sector, StringComparison.OrdinalIgnoreCase))
                return g;
        return null;
    }
}
