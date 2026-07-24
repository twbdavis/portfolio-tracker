using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Api.Endpoints;

public static class WatchlistEndpoints
{
    public record WatchlistItemDto(long Id, string Ticker, string? Name, DateTimeOffset AddedAt,
        string? Thesis, int Conviction, decimal? TargetBuyPrice, bool AlertEnabled,
        DateTimeOffset? AlertTriggeredAt, string? Notes, decimal? CurrentPrice, decimal? DistanceToTargetPct,
        decimal? DayChange, decimal? DayChangePct);

    public record WatchlistCreate(string Ticker, string? Name, string? Thesis, int Conviction,
        decimal? TargetBuyPrice, bool AlertEnabled);

    public record WatchlistUpdate(string? Thesis, int Conviction, decimal? TargetBuyPrice, bool AlertEnabled);

    public record BoughtRequest(long AccountId, decimal Shares, decimal TotalCost, DateOnly TradeDate);

    public static void MapWatchlistEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/watchlist", (CoreServices svc) =>
        {
            var quotes = svc.PriceCache.GetAll();
            return Results.Ok(svc.Watchlist.GetAll().Select(w =>
                ToDto(w, quotes.TryGetValue(w.Ticker, out var q) ? q : null)));
        });

        api.MapPost("/watchlist", (WatchlistCreate req, CoreServices svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.Ticker))
                return Results.BadRequest(new { error = "ticker is required" });
            if (req.TargetBuyPrice is decimal p && p <= 0m)
                return Results.BadRequest(new { error = "Target buy price must be a positive number." });
            var ticker = req.Ticker.Trim().ToUpperInvariant();
            if (svc.Watchlist.GetByTicker(ticker) is not null)
                return Results.Conflict(new { error = $"{ticker} is already on the watchlist" });
            var item = new WatchlistItem
            {
                Ticker = ticker,
                Name = req.Name,
                AddedAt = DateTimeOffset.Now,
                Thesis = string.IsNullOrWhiteSpace(req.Thesis) ? null : req.Thesis.Trim(),
                Conviction = Math.Clamp(req.Conviction, 1, 5),
                TargetBuyPrice = req.TargetBuyPrice,
                AlertEnabled = req.AlertEnabled && req.TargetBuyPrice is not null,
            };
            item.Id = svc.Watchlist.Insert(item);
            return Results.Ok(ToDto(item, Quote(svc, ticker)));
        });

        api.MapPut("/watchlist/{id:long}", (long id, WatchlistUpdate req, CoreServices svc) =>
        {
            if (req.TargetBuyPrice is decimal p && p <= 0m)
                return Results.BadRequest(new { error = "Target buy price must be a positive number." });
            var item = GetById(svc, id);
            if (item is null) return Results.NotFound();
            item.Thesis = string.IsNullOrWhiteSpace(req.Thesis) ? null : req.Thesis.Trim();
            item.Conviction = Math.Clamp(req.Conviction, 1, 5);
            if (item.TargetBuyPrice != req.TargetBuyPrice) item.AlertTriggeredAt = null;   // new target re-arms
            item.TargetBuyPrice = req.TargetBuyPrice;
            item.AlertEnabled = req.AlertEnabled && req.TargetBuyPrice is not null;
            svc.Watchlist.Update(item);
            return Results.Ok(ToDto(item, Quote(svc, item.Ticker)));
        });

        api.MapDelete("/watchlist/{id:long}", (long id, CoreServices svc) =>
        {
            svc.Watchlist.Delete(id);
            return Results.NoContent();
        });

        api.MapPost("/watchlist/{id:long}/rearm", (long id, CoreServices svc) =>
        {
            if (GetById(svc, id) is null) return Results.NotFound();
            svc.Watchlist.StampAlert(id, null);
            return Results.NoContent();
        });

        // "I bought it": record the lot and carry the thesis into the position's
        // notes (ResearchForm.OnBoughtIt). The WinForms flow then ASKS whether to
        // keep the watchlist row — the entry stays here and the frontend mirrors
        // that question, calling DELETE if the answer is no.
        api.MapPost("/watchlist/{id:long}/bought", (long id, BoughtRequest req, CoreServices svc) =>
        {
            var item = GetById(svc, id);
            if (item is null) return Results.NotFound();
            if (req.Shares <= 0m || req.TotalCost < 0m)
                return Results.BadRequest(new { error = "shares must be positive" });
            if (svc.Accounts.GetById(req.AccountId) is null)
                return Results.BadRequest(new { error = $"account {req.AccountId} does not exist" });

            // warning computed before the buy lands, like the WinForms confirm
            var washWarning = svc.Tax.BuyWashWarning(item.Ticker, DateOnly.FromDateTime(DateTime.Now));
            svc.ManualEntry.AddLot(req.AccountId, item.Ticker, req.Shares, req.TotalCost,
                req.TradeDate, openingBalance: false);

            if (!string.IsNullOrWhiteSpace(item.Thesis)
                && svc.Positions.GetByAccountAndTicker(req.AccountId, item.Ticker) is Position pos)
            {
                var thesis = $"Watchlist thesis ({item.Conviction}/5): {item.Thesis}";
                pos.Notes = string.IsNullOrWhiteSpace(pos.Notes) ? thesis : $"{pos.Notes}\r\n{thesis}";
                svc.Positions.Update(pos);
            }

            return Results.Ok(new { washWarning });
        });
    }

    private static WatchlistItem? GetById(CoreServices svc, long id) =>
        svc.Watchlist.GetAll().FirstOrDefault(w => w.Id == id);

    private static PriceQuote? Quote(CoreServices svc, string ticker) =>
        svc.PriceCache.GetAll().TryGetValue(ticker, out var q) ? q : null;

    private static WatchlistItemDto ToDto(WatchlistItem w, PriceQuote? q)
    {
        decimal? price = q?.Price;
        decimal? change = q is { PrevClose: decimal pc } ? q.Price - pc : null;
        return new(
            w.Id, w.Ticker, w.Name, w.AddedAt, w.Thesis, w.Conviction, w.TargetBuyPrice,
            w.AlertEnabled, w.AlertTriggeredAt, w.Notes, price,
            price is decimal p && p > 0m && w.TargetBuyPrice is decimal t ? (t - p) / p * 100m : null,
            change,
            change is decimal c && q?.PrevClose is decimal prev && prev != 0m ? c / prev * 100m : null);
    }
}
