using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Api.Services;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Api.Endpoints;

public static class SignalEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public record SignalHitDto(long Id, string Ticker, string SignalKind, DateTimeOffset FiredAt,
        bool Dismissed, string? Sector, List<SignalConditionResult> Conditions, string Summary,
        int? Strength);

    public static void MapSignalEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/signals", (bool? includeDismissed, long? universeId, CoreServices svc) =>
        {
            var hits = svc.SignalHits.GetAll(includeDismissed ?? false);
            if (universeId is long id)
            {
                var members = svc.Universes.GetMembers(id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // sector-level hits (BreadthThrust) key on the sector name, so
                // a universe filter keeps them only when the universe holds a
                // ticker of that sector — approximate by keeping them always
                hits = hits.Where(h => h.SignalKind == SignalKinds.BreadthThrust
                                       || members.Contains(h.Ticker)).ToList();
            }
            return Results.Ok(hits.Select(h =>
            {
                var why = ParseWhy(h.InputsJson);
                return new SignalHitDto(h.Id, h.Ticker, h.SignalKind, h.FiredAt, h.Dismissed,
                    why.Sector, why.Conditions, why.Summary, why.Strength);
            }));
        });

        api.MapGet("/signals/ticker/{ticker}", (string ticker, CoreServices svc) =>
            Results.Ok(svc.SignalHits.GetForTicker(ticker.Trim().ToUpperInvariant()).Select(h =>
            {
                var why = ParseWhy(h.InputsJson);
                return new SignalHitDto(h.Id, h.Ticker, h.SignalKind, h.FiredAt, h.Dismissed,
                    why.Sector, why.Conditions, why.Summary, why.Strength);
            })));

        api.MapPost("/signals/{id:long}/dismiss", (long id, CoreServices svc) =>
        {
            svc.SignalHits.Dismiss(id, DateTimeOffset.Now);
            return Results.NoContent();
        });

        // "Re-evaluate signals": deterministic re-run over current warehouse state
        api.MapPost("/signals/evaluate", async (SignalService signals, CoreServices svc, CancellationToken ct) =>
        {
            var fired = await signals.EvaluateAsync(ct);
            return Results.Ok(new
            {
                fired,
                lastEvaluatedAt = svc.AppState.Get(SignalService.LastEvaluatedKey),
            });
        });
    }

    private static SignalWhy ParseWhy(string inputsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<SignalWhy>(inputsJson, JsonOpts)
                   ?? new SignalWhy("", null, new List<SignalConditionResult>());
        }
        catch (JsonException)
        {
            return new SignalWhy("", null, new List<SignalConditionResult>());
        }
    }
}
