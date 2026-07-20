using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Import;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Api.Endpoints;

/// Import is two phases exactly like the WinForms flow: preview parses and
/// finds unknown accounts without writing anything; commit routes rows after
/// every unknown account has a decision. The parsed file is held server-side
/// under a preview id so the file uploads once.
public class ImportSessions
{
    private readonly ConcurrentDictionary<string, (ImportPreview Preview, string FileName, DateTimeOffset At)> _sessions = new();

    public string Add(ImportPreview preview, string fileName)
    {
        // sessions are per-launch and tiny; still, drop anything stale
        foreach (var (key, value) in _sessions)
            if (value.At < DateTimeOffset.UtcNow.AddHours(-1))
                _sessions.TryRemove(key, out _);
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = (preview, fileName, DateTimeOffset.UtcNow);
        return id;
    }

    public (ImportPreview Preview, string FileName)? Take(string id) =>
        _sessions.TryRemove(id, out var s) ? (s.Preview, s.FileName) : null;
}

public static class ImportEndpoints
{
    public record UnknownAccountDto(string AccountNumber, string? AccountNameRaw, string SuggestedName,
        TaxTreatment SuggestedTreatment, string? Evidence);

    public record PreviewResponse(string PreviewId, int TradeRows, int CashRows, int SkippedKnown,
        Dictionary<string, int> UnrecognizedTypes, List<UnknownAccountDto> UnknownAccounts,
        string? AdoptedByPlaceholder);

    public record HeaderMismatchResponse(List<string> ActualHeaders, string[] RequiredFields);

    public record NewAccountDecisionDto(string AccountNumber, string DisplayName, TaxTreatment Treatment);

    public record CommitRequest(string PreviewId, List<NewAccountDecisionDto> NewAccounts);

    public static void MapImportEndpoints(this IEndpointRouteBuilder api)
    {
        // multipart upload; optional columnMap form field (JSON field→header)
        api.MapPost("/import/preview", async (HttpRequest request, CoreServices svc, ImportSessions sessions) =>
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null) return Results.BadRequest(new { error = "no file uploaded" });

            var map = ColumnMap.Default;
            if (form.TryGetValue("columnMap", out var mapJson) && mapJson.Count > 0)
            {
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(mapJson[0]!);
                if (dict is not null) map = new ColumnMap(dict);
            }

            // header validation first, so the wizard can offer remapping instead of failing
            await using (var probe = file.OpenReadStream())
            {
                var mismatch = ChaseCsvParser.ValidateHeaders(probe);
                if (mismatch is not null && ReferenceEquals(map, ColumnMap.Default))
                    return Results.UnprocessableEntity(new HeaderMismatchResponse(mismatch, ColumnMap.RequiredFields));
            }

            await using var stream = file.OpenReadStream();
            ImportPreview preview;
            try { preview = svc.Importer.Preview(stream, map); }
            catch (Exception ex) { return Results.BadRequest(new { error = $"Could not parse the file: {ex.Message}" }); }

            var id = sessions.Add(preview, file.FileName);
            return Results.Ok(new PreviewResponse(id,
                preview.Parsed.Trades.Count, preview.Parsed.CashFlows.Count,
                preview.Parsed.SkippedKnown, preview.Parsed.UnrecognizedTypes,
                preview.UnknownAccounts.Select(u => new UnknownAccountDto(u.AccountNumber, u.AccountNameRaw,
                    u.SuggestedName, u.SuggestedTreatment, u.Evidence)).ToList(),
                preview.AdoptedByPlaceholder));
        });

        api.MapPost("/import/commit", (CommitRequest req, CoreServices svc, ImportSessions sessions) =>
        {
            var session = sessions.Take(req.PreviewId);
            if (session is null) return Results.NotFound(new { error = "preview expired — upload the file again" });
            var (preview, fileName) = session.Value;

            var decisions = req.NewAccounts.ToDictionary(d => d.AccountNumber, StringComparer.OrdinalIgnoreCase);
            foreach (var suggestion in preview.UnknownAccounts)
            {
                // default to the auto-suggestion when the wizard sent no override
                var decision = decisions.TryGetValue(suggestion.AccountNumber, out var d)
                    ? new NewAccountDecision(d.DisplayName, d.Treatment)
                    : new NewAccountDecision(suggestion.SuggestedName, suggestion.SuggestedTreatment);
                svc.Importer.CreateAccount(suggestion, decision);
            }
            var result = svc.Importer.Commit(preview, fileName);
            return Results.Ok(result);
        });

        api.MapGet("/import/batches", (CoreServices svc) =>
        {
            var accounts = svc.Accounts.GetAll().ToDictionary(a => a.Id, a => a.DisplayName);
            return Results.Ok(svc.Batches.GetAll().OrderByDescending(b => b.ImportedAt).Select(b => new
            {
                b.Id, b.AccountId,
                Account = accounts.GetValueOrDefault(b.AccountId, "?"),
                b.ImportedAt, b.FileName, b.RowsImported, b.RowsSkipped, b.RowsDeduped,
            }));
        });

        api.MapDelete("/import/batches/{id:long}", (long id, CoreServices svc) =>
        {
            svc.Importer.DeleteBatch(id);
            return Results.NoContent();
        });
    }
}
