using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using PortfolioTracker.Api.Services;

namespace PortfolioTracker.Api.Endpoints;

public static class CrawlerEndpoints
{
    public static void MapCrawlerEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/crawler/status", (UniverseCrawler crawler) =>
            Results.Ok(crawler.GetStatus()));

        // "Refresh universe now" — fire and forget; progress shows in status.
        // Bound to the host lifetime, not the request: the crawl must outlive
        // the HTTP call that started it.
        api.MapPost("/crawler/run", (long? universeId, UniverseCrawler crawler,
            IHostApplicationLifetime lifetime) =>
        {
            _ = Task.Run(() => crawler.RunPassAsync(universeId, lifetime.ApplicationStopping));
            return Results.Ok(new { started = true });
        });
    }
}
