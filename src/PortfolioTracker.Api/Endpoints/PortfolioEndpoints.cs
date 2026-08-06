using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Api.Services;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Api.Endpoints;

public static class PortfolioEndpoints
{
    public static void MapPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        api.MapAccountEndpoints();
        api.MapQuoteEndpoints();
        api.MapDashboardEndpoints();
        api.MapPositionEndpoints();
        api.MapTargetEndpoints();
        api.MapWatchlistEndpoints();
        api.MapResearchEndpoints();
        api.MapResearchQueueEndpoints();
        api.MapUniverseEndpoints();
        api.MapCrawlerEndpoints();
        api.MapSectorEndpoints();
        api.MapSignalEndpoints();
        api.MapSettingsEndpoints();
        api.MapImportEndpoints();
        api.MapTaxEndpoints();
        api.MapSimulatorEndpoints();
    }

    // ---- accounts -----------------------------------------------------------

    public record AccountDto(long Id, string AccountNumber, string DisplayName, string? AccountNameRaw,
        TaxTreatment TaxTreatment, string TreatmentLabel, bool IsRetirement, int SortOrder, bool NeedsReview);

    public record AccountPatch(string? DisplayName, string? AccountNumber, TaxTreatment? TaxTreatment, int? SortOrder);

    private static AccountDto ToDto(Account a) => new(a.Id, a.AccountNumber, a.DisplayName, a.AccountNameRaw,
        a.TaxTreatment, PortfolioCalculator.TreatmentLabel(a.TaxTreatment), a.IsRetirement, a.SortOrder, a.NeedsReview);

    private static void MapAccountEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/accounts", (CoreServices svc) =>
            Results.Ok(svc.Accounts.GetAll().Select(ToDto)));

        api.MapPatch("/accounts/{id:long}", (long id, AccountPatch patch, CoreServices svc) =>
        {
            var account = svc.Accounts.GetById(id);
            if (account is null) return Results.NotFound();
            if (patch.DisplayName is not null) account.DisplayName = patch.DisplayName;
            if (patch.AccountNumber is not null) account.AccountNumber = patch.AccountNumber;
            if (patch.TaxTreatment is not null) account.TaxTreatment = patch.TaxTreatment.Value;
            if (patch.SortOrder is not null) account.SortOrder = patch.SortOrder.Value;
            account.NeedsReview = false;   // any explicit edit clears the migration prompt
            svc.Accounts.Update(account);
            return Results.Ok(ToDto(account));
        });
    }

    // ---- quotes -------------------------------------------------------------

    private static void MapQuoteEndpoints(this IEndpointRouteBuilder api)
    {
        // manual "Refresh Prices"; the resulting quotes also go out over SignalR
        api.MapPost("/quotes/refresh", async (QuotePoller poller, CancellationToken ct) =>
        {
            var result = await poller.RefreshNowAsync(ct);
            return Results.Ok(new
            {
                refreshed = result is not null,
                error = result?.Error,
                unresolved = result?.Unresolved ?? new List<string>(),
                count = result?.Quotes.Count ?? 0,
            });
        });

        api.MapGet("/quotes", (CoreServices svc) =>
            Results.Ok(svc.PriceCache.GetAll().Values
                .Select(q => new Hubs.QuotePush(q.Ticker, q.Price, q.PrevClose, q.FetchedAt))));
    }
}
