using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PortfolioTracker.Api.Endpoints;

/// Universe CRUD + membership. Everything here is instant local SQLite; the
/// crawler notices membership changes on its next staleness sweep.
public static class UniverseEndpoints
{
    public record UniverseDto(long Id, string Name, bool IsSeeded, int MemberCount);
    public record NameBody(string Name);
    public record TickersBody(List<string> Tickers);

    public static void MapUniverseEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/universes", (CoreServices svc) =>
        {
            var counts = svc.Universes.GetMemberCounts();
            return Results.Ok(svc.Universes.GetAll().Select(u =>
                new UniverseDto(u.Id, u.Name, u.IsSeeded, counts.GetValueOrDefault(u.Id))));
        });

        api.MapPost("/universes", (NameBody body, CoreServices svc) =>
        {
            var name = body.Name.Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "name is required" });
            if (svc.Universes.GetAll().Any(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = $"universe \"{name}\" already exists" });
            var id = svc.Universes.Insert(name, isSeeded: false);
            return Results.Ok(new UniverseDto(id, name, false, 0));
        });

        api.MapPut("/universes/{id:long}", (long id, NameBody body, CoreServices svc) =>
        {
            if (svc.Universes.Get(id) is null) return Results.NotFound();
            var name = body.Name.Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "name is required" });
            svc.Universes.Rename(id, name);
            return Results.NoContent();
        });

        api.MapDelete("/universes/{id:long}", (long id, CoreServices svc) =>
        {
            svc.Universes.Delete(id);
            return Results.NoContent();
        });

        api.MapGet("/universes/{id:long}/members", (long id, CoreServices svc) =>
            svc.Universes.Get(id) is null
                ? Results.NotFound()
                : Results.Ok(svc.Universes.GetMembers(id)));

        api.MapPost("/universes/{id:long}/members", (long id, TickersBody body, CoreServices svc) =>
        {
            if (svc.Universes.Get(id) is null) return Results.NotFound();
            svc.Universes.AddMembers(id, body.Tickers);
            return Results.Ok(svc.Universes.GetMembers(id));
        });

        api.MapDelete("/universes/{id:long}/members/{ticker}", (long id, string ticker, CoreServices svc) =>
        {
            svc.Universes.RemoveMember(id, ticker.Trim().ToUpperInvariant());
            return Results.NoContent();
        });

        // convenience sources: current holdings, the watchlist, or every
        // already-classified ticker in a sector
        api.MapPost("/universes/{id:long}/members/from-holdings", (long id, CoreServices svc) =>
        {
            if (svc.Universes.Get(id) is null) return Results.NotFound();
            var open = svc.Lots.GetAllOpen().GroupBy(l => l.PositionId)
                .Where(g => g.Sum(l => l.RemainingShares) != 0m)
                .Select(g => g.Key).ToHashSet();
            var tickers = svc.Positions.GetAll().Where(p => open.Contains(p.Id)).Select(p => p.Ticker);
            svc.Universes.AddMembers(id, tickers);
            return Results.Ok(svc.Universes.GetMembers(id));
        });

        api.MapPost("/universes/{id:long}/members/from-watchlist", (long id, CoreServices svc) =>
        {
            if (svc.Universes.Get(id) is null) return Results.NotFound();
            svc.Universes.AddMembers(id, svc.Watchlist.GetAll().Select(w => w.Ticker));
            return Results.Ok(svc.Universes.GetMembers(id));
        });

        api.MapPost("/universes/{id:long}/members/from-sector", (long id, string sector, CoreServices svc) =>
        {
            if (svc.Universes.Get(id) is null) return Results.NotFound();
            var tickers = svc.Classifications.GetAll()
                .Where(c => string.Equals(c.Sector, sector, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Ticker);
            svc.Universes.AddMembers(id, tickers);
            return Results.Ok(svc.Universes.GetMembers(id));
        });

        // sector/industry user override (Source=User survives future crawls)
        api.MapPut("/classification/{ticker}", (string ticker, ClassificationBody body, CoreServices svc) =>
        {
            svc.Classifications.SetUserOverride(ticker,
                string.IsNullOrWhiteSpace(body.Sector) ? null : body.Sector.Trim(),
                string.IsNullOrWhiteSpace(body.Industry) ? null : body.Industry.Trim());
            return Results.NoContent();
        });
    }

    public record ClassificationBody(string? Sector, string? Industry);
}
