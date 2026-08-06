using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;
using PortfolioTracker.Core.Research;

namespace PortfolioTracker.Api.Endpoints;

public static class SettingsEndpoints
{
    public record DayStartRequest(decimal? Value);

    public record SettingsDto(string? FinnhubApiKey, int RefreshIntervalSeconds, bool ShowClosedPositions,
        string Theme, FilingStatus FilingStatus, decimal OtherIncome, bool StartWithWindows,
        bool AutoAcceptDiscoveries, string? AnthropicApiKey, string AnthropicModel,
        int AiMonthlyTokenBudget, bool AiWeeklyBatchEnabled, int AiWeeklyBatchCount,
        bool ScoreboardDownWeighting,
        string AiProvider, string? OpenWebUiBaseUrl, string? OpenWebUiApiKey, string? OpenWebUiModel,
        int OpenWebUiTimeoutSeconds, bool OpenWebUiDisableThinking);

    public record SettingsPut(string? FinnhubApiKey, int RefreshIntervalSeconds, bool ShowClosedPositions,
        string Theme, FilingStatus FilingStatus, decimal OtherIncome, bool StartWithWindows,
        bool AutoAcceptDiscoveries, string? AnthropicApiKey, string? AnthropicModel,
        int AiMonthlyTokenBudget, bool AiWeeklyBatchEnabled, int AiWeeklyBatchCount,
        bool ScoreboardDownWeighting,
        string? AiProvider, string? OpenWebUiBaseUrl, string? OpenWebUiApiKey, string? OpenWebUiModel,
        int OpenWebUiTimeoutSeconds, bool OpenWebUiDisableThinking = true);

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
            StartupRegistration.IsEnabled(),
            svc.Settings.AutoAcceptDiscoveries, svc.Settings.AnthropicApiKey,
            svc.Settings.AnthropicModel, svc.Settings.AiMonthlyTokenBudget,
            svc.Settings.AiWeeklyBatchEnabled, svc.Settings.AiWeeklyBatchCount,
            svc.Settings.ScoreboardDownWeighting,
            svc.Settings.AiProvider ?? BriefProviderKinds.Disabled,
            svc.Settings.OpenWebUiBaseUrl, svc.Settings.OpenWebUiApiKey,
            svc.Settings.OpenWebUiModel, svc.Settings.OpenWebUiTimeoutSeconds,
            svc.Settings.OpenWebUiDisableThinking)));

        api.MapPut("/settings", (SettingsPut req, CoreServices svc) =>
        {
            if (req.RefreshIntervalSeconds is < 10 or > 3600)
                return Results.BadRequest(new { error = "refresh interval must be 10–3600 seconds" });
            if (req.OtherIncome < 0m)
                return Results.BadRequest(new { error = "other income must be a non-negative number" });
            if (req.AiMonthlyTokenBudget < 0)
                return Results.BadRequest(new { error = "AI token budget must be non-negative" });
            if (req.AiWeeklyBatchCount > 20)
                return Results.BadRequest(new { error = "weekly brief batch is capped at 20 candidates" });
            var key = req.FinnhubApiKey?.Trim();
            svc.Settings.FinnhubApiKey = string.IsNullOrEmpty(key) ? null : key;
            svc.Settings.RefreshIntervalSeconds = req.RefreshIntervalSeconds;
            svc.Settings.ShowClosedPositions = req.ShowClosedPositions;
            svc.Settings.Theme = req.Theme == "light" ? "light" : "dark";
            svc.Settings.AutoAcceptDiscoveries = req.AutoAcceptDiscoveries;
            var aiKey = req.AnthropicApiKey?.Trim();
            svc.Settings.AnthropicApiKey = string.IsNullOrEmpty(aiKey) ? null : aiKey;
            svc.Settings.AnthropicModel = string.IsNullOrWhiteSpace(req.AnthropicModel)
                ? AnthropicClient.DefaultModel : req.AnthropicModel.Trim();
            svc.Settings.AiMonthlyTokenBudget = req.AiMonthlyTokenBudget > 0
                ? req.AiMonthlyTokenBudget : new AppSettings().AiMonthlyTokenBudget;
            svc.Settings.AiWeeklyBatchEnabled = req.AiWeeklyBatchEnabled;
            // absent/zero from an older client keeps the default batch size
            svc.Settings.AiWeeklyBatchCount = req.AiWeeklyBatchCount >= 1
                ? req.AiWeeklyBatchCount : new AppSettings().AiWeeklyBatchCount;
            svc.Settings.ScoreboardDownWeighting = req.ScoreboardDownWeighting;
            var provider = req.AiProvider?.Trim().ToLowerInvariant();
            if (provider is BriefProviderKinds.OpenWebUi or BriefProviderKinds.Anthropic
                or BriefProviderKinds.Disabled)
                svc.Settings.AiProvider = provider;
            var baseUrl = req.OpenWebUiBaseUrl?.Trim().TrimEnd('/');
            svc.Settings.OpenWebUiBaseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl;
            var owKey = req.OpenWebUiApiKey?.Trim();
            svc.Settings.OpenWebUiApiKey = string.IsNullOrEmpty(owKey) ? null : owKey;
            var owModel = req.OpenWebUiModel?.Trim();
            svc.Settings.OpenWebUiModel = string.IsNullOrEmpty(owModel) ? null : owModel;
            // absent/zero from an older client keeps the generous default
            svc.Settings.OpenWebUiTimeoutSeconds = req.OpenWebUiTimeoutSeconds >= 10
                ? Math.Clamp(req.OpenWebUiTimeoutSeconds, 10, 3600)
                : new AppSettings().OpenWebUiTimeoutSeconds;
            svc.Settings.OpenWebUiDisableThinking = req.OpenWebUiDisableThinking;
            svc.Settings.Save();
            svc.Tax.Settings.SetFilingStatus(req.FilingStatus);
            svc.Tax.Settings.SetOtherIncome(req.OtherIncome);
            if (req.StartWithWindows) StartupRegistration.Enable();   // rewrites the exe path
            else StartupRegistration.Disable();
            return Results.NoContent();
        });

        // 1-token ping through a brief provider; surfaces HTTP/SSL/auth errors
        // readably. The optional body carries the Settings form's CURRENT
        // values so testing works before saving; an empty body tests the
        // saved configuration. Body is read by hand — [FromBody] would 415 a
        // bodyless POST.
        api.MapPost("/settings/test-ai",
            async (HttpRequest request, Services.BriefService briefs, CancellationToken ct) =>
        {
            Services.BriefService.TestAiRequest? req = null;
            if (request.HasJsonContentType())
            {
                try { req = await request.ReadFromJsonAsync<Services.BriefService.TestAiRequest>(ct); }
                catch (System.Text.Json.JsonException) { /* malformed/empty: test saved config */ }
            }
            var (ok, error) = await briefs.TestConnectionAsync(req, ct);
            return Results.Ok(new { ok, error });
        });

        // re-seed {appdata}/prompts/brief/ from the embedded defaults
        api.MapPost("/settings/prompts/reset", (CoreServices svc) =>
        {
            svc.BriefPrompts.Seed(overwrite: true);
            return Results.Ok(new { dir = svc.BriefPrompts.Dir });
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
