using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Api.Endpoints;

public static class TargetEndpoints
{
    public record TargetDto(long Id, string Ticker, decimal? TargetPrice, TargetDirection TargetDirection,
        bool AlertEnabled, DateTimeOffset? AlertTriggeredAt, string? Notes);

    public record TargetPut(decimal? TargetPrice, TargetDirection TargetDirection, bool AlertEnabled, string? Notes);

    private static TargetDto ToDto(TickerTarget t) => new(t.Id, t.Ticker, t.TargetPrice,
        t.TargetDirection, t.AlertEnabled, t.AlertTriggeredAt, t.Notes);

    public static void MapTargetEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/targets", (CoreServices svc) =>
            Results.Ok(svc.TickerTargets.GetAll().Select(ToDto)));

        // no-target tickers get an empty dto (id 0) — the same "editor over a
        // blank TickerTarget" shape PositionDetailForm used
        api.MapGet("/targets/{ticker}", (string ticker, CoreServices svc) =>
        {
            ticker = ticker.Trim().ToUpperInvariant();
            var target = svc.TickerTargets.GetByTicker(ticker) ?? new TickerTarget { Ticker = ticker };
            return Results.Ok(ToDto(target));
        });

        api.MapPut("/targets/{ticker}", (string ticker, TargetPut req, CoreServices svc) =>
        {
            if (req.TargetPrice is decimal p && p <= 0m)
                return Results.BadRequest(new { error = "Target price must be a positive number." });
            ticker = ticker.Trim().ToUpperInvariant();
            var target = svc.TickerTargets.GetByTicker(ticker) ?? new TickerTarget { Ticker = ticker };
            if (req.TargetPrice != target.TargetPrice) target.AlertTriggeredAt = null;   // changing target re-arms
            target.TargetPrice = req.TargetPrice;
            target.TargetDirection = req.TargetDirection;
            target.AlertEnabled = req.AlertEnabled;
            target.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
            svc.TickerTargets.Upsert(target);
            return Results.Ok(ToDto(svc.TickerTargets.GetByTicker(ticker)!));
        });

        api.MapDelete("/targets/{ticker}", (string ticker, CoreServices svc) =>
        {
            svc.TickerTargets.Delete(ticker.Trim().ToUpperInvariant());
            return Results.NoContent();
        });

        api.MapPost("/targets/{ticker}/rearm", (string ticker, CoreServices svc) =>
        {
            svc.TickerTargets.StampAlert(ticker.Trim().ToUpperInvariant(), null);
            return Results.NoContent();
        });
    }
}
