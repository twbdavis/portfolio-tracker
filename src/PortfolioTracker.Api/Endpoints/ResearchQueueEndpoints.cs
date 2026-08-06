using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Api.Services;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Api.Endpoints;

/// The research priority queue, discovery inbox, and AI briefs. All queue
/// reads are warehouse-only (no live API calls); briefs are the single
/// endpoint here that talks to an external service, and only on an explicit
/// POST.
public static class ResearchQueueEndpoints
{
    public record EvidenceDto(string Dimension, string Label, string Status, string Observed,
        string Threshold, string? AsOf, decimal Weight, decimal AppliedWeight, string? Note);

    public record QueueRowDto(string Ticker, string? Sector, decimal Score,
        decimal WeightedPasses, decimal ApplicableWeight, decimal? FundamentalsRank,
        bool IsHeld, bool OnWatchlist, string FitLine, long? LatestBriefId,
        List<EvidenceDto> Evidence);

    public record SectorWeightDto(string Sector, decimal WeightPct);

    public record QueueDto(List<QueueRowDto> Rows, List<SectorWeightDto> PortfolioSectorWeights,
        int HoldingsCount, int SnoozedCount, string? JobsLastRanAt, bool DownWeightingActive);

    public record DiscoveryDto(string Ticker, string SourceTicker, DateTimeOffset DiscoveredAt);

    public record AcceptRequest(long? UniverseId);

    public record BriefDto(long Id, string Ticker, DateTimeOffset CreatedAt, string Model,
        string BriefMarkdown, int TokensUsed, bool NeedsReview, string? InputSnapshotJson);

    public record TraceDto(long Id, string Section, string Stage, string UserPrompt,
        string RawResponse, string CleanedText, string ViolationsJson, bool Passed,
        DateTimeOffset CreatedAt);

    public record BriefSummaryDto(long Id, string Ticker, DateTimeOffset CreatedAt, string Model,
        int TokensUsed, bool NeedsReview);

    public static void MapResearchQueueEndpoints(this IEndpointRouteBuilder api)
    {
        // ---- queue ------------------------------------------------------------

        api.MapGet("/queue", (bool? includeHeld, CoreServices svc, QueueService queue) =>
        {
            var built = queue.Build(includeHeld ?? false);
            var watchlist = svc.Watchlist.GetAll().Select(w => w.Ticker)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var briefIds = svc.Briefs.GetLatestIdByTicker();

            var rows = built.Candidates.Select(c => new QueueRowDto(
                c.Ticker, c.Sector == SectorAnalyzer.Unclassified ? null : c.Sector,
                Math.Round(c.Score, 4), c.WeightedPasses, c.ApplicableWeight,
                c.FundamentalsRank is decimal fr ? Math.Round(fr, 2) : null,
                built.HeldTickers.Contains(c.Ticker),
                watchlist.Contains(c.Ticker),
                queue.FitLine(c.Ticker, c.Sector, built.PortfolioSectorWeights,
                    built.FitByTicker, built.Settings),
                briefIds.TryGetValue(c.Ticker, out var briefId) ? briefId : null,
                c.Evidence.Select(e => new EvidenceDto(e.Dimension, e.Label, e.Status.ToString(),
                    e.Observed, e.Threshold, e.AsOf, e.Weight, e.AppliedWeight, e.Note)).ToList()
            )).ToList();

            return Results.Ok(new QueueDto(rows,
                built.PortfolioSectorWeights
                    .Select(w => new SectorWeightDto(w.Sector, Math.Round(w.WeightPct, 1))).ToList(),
                built.HeldTickers.Count,
                built.Snoozed.Count,
                svc.AppState.Get(ResearchJobsService.LastRunKey),
                svc.Settings.ScoreboardDownWeighting));
        });

        api.MapPost("/queue/{ticker}/dismiss", (string ticker, CoreServices svc, QueueService queue) =>
        {
            var days = queue.LoadSettings().SnoozeDays;
            svc.QueueSnoozes.Snooze(ticker, DateTimeOffset.Now.AddDays(days));
            return Results.Ok(new { snoozedUntil = DateTimeOffset.Now.AddDays(days) });
        });

        // manual run of the nightly research jobs (fit + outcomes + batch)
        api.MapPost("/queue/jobs/run", async (ResearchJobsService jobs, CancellationToken ct) =>
            Results.Ok(await jobs.RunAsync(ct)));

        // ---- discoveries --------------------------------------------------------

        api.MapGet("/discoveries", (CoreServices svc) =>
            Results.Ok(svc.Discovered.GetAll(DiscoveryStatus.New)
                .Select(d => new DiscoveryDto(d.Ticker, d.SourceTicker, d.DiscoveredAt))));

        api.MapPost("/discoveries/{ticker}/accept",
            (string ticker, AcceptRequest req, CoreServices svc, UniverseCrawler crawler) =>
        {
            var discovery = svc.Discovered.Get(ticker);
            if (discovery is null) return Results.NotFound();

            long universeId;
            if (req.UniverseId is long id)
            {
                if (svc.Universes.Get(id) is null)
                    return Results.BadRequest(new { error = "unknown universe" });
                universeId = id;
            }
            else
            {
                var existing = svc.Universes.GetAll().FirstOrDefault(u =>
                    u.Name.Equals(UniverseCrawler.DiscoveredUniverseName, StringComparison.OrdinalIgnoreCase));
                universeId = existing?.Id
                    ?? svc.Universes.Insert(UniverseCrawler.DiscoveredUniverseName, isSeeded: false);
            }

            svc.Universes.AddMembers(universeId, new[] { discovery.Ticker });
            svc.Discovered.SetStatus(discovery.Ticker, DiscoveryStatus.Accepted);
            // kick a crawl pass so the new ticker gets profiled/warehoused soon;
            // not the request token — the pass outlives this response
            _ = crawler.RunPassAsync(null, CancellationToken.None);
            return Results.Ok(new { ticker = discovery.Ticker, universeId });
        });

        api.MapPost("/discoveries/{ticker}/ignore", (string ticker, CoreServices svc) =>
        {
            if (svc.Discovered.Get(ticker) is null) return Results.NotFound();
            svc.Discovered.SetStatus(ticker, DiscoveryStatus.Ignored);
            return Results.NoContent();
        });

        // ---- AI briefs -----------------------------------------------------------

        // Starts the BriefAgent pipeline in the background (a local model can
        // legitimately take minutes). 200 + brief = idempotency hit for this
        // exact input snapshot; 202 = generation started, poll /briefs/status.
        // ?force=true regenerates even with unchanged data.
        api.MapPost("/research/{ticker}/brief",
            async (string ticker, bool? force, BriefService briefs, CancellationToken ct) =>
        {
            try
            {
                var existing = await briefs.RequestGenerationAsync(ticker, force ?? false, ct);
                return existing is not null
                    ? Results.Ok(ToDto(existing, includeSnapshot: false))
                    : Results.Accepted($"/api/briefs/status", new { started = true });
            }
            catch (BriefGenerationException ex)
            {
                // surfaced, never thrown at the client as a 500
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
        });

        // pipeline progress for the dialog's per-stage state
        api.MapGet("/briefs/status", (BriefService briefs) => Results.Ok(briefs.Status));

        // the per-stage transcript behind "View generation trace"
        api.MapGet("/briefs/{id:long}/trace", (long id, CoreServices svc) =>
            svc.Briefs.Get(id) is null
                ? Results.NotFound()
                : Results.Ok(svc.BriefTraces.GetForBrief(id).Select(t => new TraceDto(
                    t.Id, t.Section, t.Stage, t.UserPrompt, t.RawResponse, t.CleanedText,
                    t.ViolationsJson, t.Passed, t.CreatedAt))));

        api.MapGet("/research/{ticker}/briefs", (string ticker, CoreServices svc) =>
            Results.Ok(svc.Briefs.GetForTicker(ticker)
                .Select(b => ToDto(b, includeSnapshot: false))));

        // full history for the Briefs tab: summaries only, bodies load per brief
        api.MapGet("/briefs", (CoreServices svc) =>
            Results.Ok(svc.Briefs.GetAllSummaries().Select(b => new BriefSummaryDto(
                b.Id, b.Ticker, b.CreatedAt, b.Model, b.TokensUsed, b.NeedsReview))));

        // full brief including the exact input snapshot that produced it
        api.MapGet("/briefs/{id:long}", (long id, CoreServices svc) =>
            svc.Briefs.Get(id) is { } row
                ? Results.Ok(ToDto(row, includeSnapshot: true))
                : Results.NotFound());

        api.MapGet("/ai/usage", (BriefService briefs) => Results.Ok(briefs.Usage()));
    }

    private static BriefDto ToDto(ResearchBriefRow row, bool includeSnapshot) =>
        new(row.Id, row.Ticker, row.CreatedAt, row.Model, row.BriefMarkdown, row.TokensUsed,
            row.NeedsReview, includeSnapshot ? row.InputSnapshotJson : null);
}
