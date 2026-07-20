using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Api.Endpoints;

public static class SettingsEndpoints
{
    public record DayStartRequest(decimal? Value);

    public record SettingsDto(string? FinnhubApiKey, int RefreshIntervalSeconds, bool ShowClosedPositions,
        string Theme, FilingStatus FilingStatus, decimal OtherIncome, bool StartWithWindows);

    public record SettingsPut(string? FinnhubApiKey, int RefreshIntervalSeconds, bool ShowClosedPositions,
        string Theme, FilingStatus FilingStatus, decimal OtherIncome, bool StartWithWindows);

    public record AccountSummaryDto(long Id, string DisplayName, string Number, string Treatment,
        int Positions, int Transactions);

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder api)
    {
        // manual Daily Gain/Loss baseline; only honored today, cumulative view only
        api.MapPut("/settings/daystart", (DayStartRequest req, CoreServices svc) =>
        {
            svc.Settings.DayStartValue = req.Value;
            svc.Settings.DayStartDate = req.Value is null ? null : DateOnly.FromDateTime(DateTime.Now);
            svc.Settings.Save();
            return Results.NoContent();
        });

        api.MapGet("/settings", (CoreServices svc) => Results.Ok(new SettingsDto(
            svc.Settings.FinnhubApiKey, svc.Settings.RefreshIntervalSeconds,
            svc.Settings.ShowClosedPositions, svc.Settings.Theme,
            svc.Tax.Settings.GetFilingStatus(), svc.Tax.Settings.GetOtherIncome(),
            StartupRegistration.IsEnabled())));

        api.MapPut("/settings", (SettingsPut req, CoreServices svc) =>
        {
            if (req.RefreshIntervalSeconds is < 10 or > 3600)
                return Results.BadRequest(new { error = "refresh interval must be 10–3600 seconds" });
            if (req.OtherIncome < 0m)
                return Results.BadRequest(new { error = "other income must be a non-negative number" });
            var key = req.FinnhubApiKey?.Trim();
            svc.Settings.FinnhubApiKey = string.IsNullOrEmpty(key) ? null : key;
            svc.Settings.RefreshIntervalSeconds = req.RefreshIntervalSeconds;
            svc.Settings.ShowClosedPositions = req.ShowClosedPositions;
            svc.Settings.Theme = req.Theme == "light" ? "light" : "dark";
            svc.Settings.Save();
            svc.Tax.Settings.SetFilingStatus(req.FilingStatus);
            svc.Tax.Settings.SetOtherIncome(req.OtherIncome);
            if (req.StartWithWindows) StartupRegistration.Enable();   // rewrites the exe path
            else StartupRegistration.Disable();
            return Results.NoContent();
        });

        // the SettingsForm "connection test": one real quote through the provider
        api.MapPost("/settings/test-connection", async (CoreServices svc, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(svc.Settings.FinnhubApiKey))
                return Results.Ok(new { ok = false, error = "No API key configured." });
            var result = await svc.Prices.RefreshAsync(new[] { "AAPL" }, ct);
            return Results.Ok(new
            {
                ok = result.Error is null && result.Quotes.Count > 0,
                error = result.Error ?? (result.Quotes.Count == 0 ? "No quote returned." : null),
            });
        });

        // accounts panel: row counts plus the cascade delete
        api.MapGet("/settings/accounts", (CoreServices svc) =>
        {
            var positionsByAccount = svc.Positions.GetAll().ToLookup(p => p.AccountId);
            return Results.Ok(svc.Accounts.GetAll().Select(a => new AccountSummaryDto(
                a.Id, a.DisplayName,
                a.AccountNumber.Length == 0 ? "(not set)" : a.AccountNumber,
                PortfolioCalculator.TreatmentLabel(a.TaxTreatment),
                positionsByAccount[a.Id].Count(),
                positionsByAccount[a.Id].Sum(p => svc.Transactions.GetByAccountAndTicker(a.Id, p.Ticker).Count))));
        });

        api.MapDelete("/accounts/{id:long}", (long id, CoreServices svc) =>
        {
            if (svc.Accounts.GetById(id) is null) return Results.NotFound();
            svc.ManualEntry.DeleteAccount(id);
            return Results.NoContent();
        });
    }
}
