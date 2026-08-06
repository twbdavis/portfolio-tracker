using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Pricing;
using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Api.Services;

/// The BriefAgent host: builds the whitelisted payload, runs the sectioned
/// BriefPipeline against whichever provider AI_VISION_PROVIDER selects, and
/// persists brief + per-stage trace. Guarantees, regardless of provider:
/// strictly sequential model calls (one global lock — the local server is
/// shared company hardware), idempotency per (ticker, snapshot hash), resume
/// of an interrupted brief from its passing sections, and no unverified
/// numbers stored without a NeedsReview flag.
public class BriefService(CoreServices svc, QueueService queue, ILogger<BriefService> log)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sequential = new(1, 1);
    private volatile RunStatus _status = RunStatus.Idle;

    public record UsageInfo(string Month, int TokensUsed, int Budget, bool Enabled,
        string Provider, bool BudgetEnforced);

    /// Polled by the UI while a brief generates: which section, how many
    /// calls, and the last error if the run died.
    public record RunStatus(bool Active, string? Ticker, string? Stage, int SectionIndex,
        int TotalSections, DateTimeOffset? StartedAt, string? LastError, long? LastBriefId)
    {
        public static readonly RunStatus Idle = new(false, null, null, 0,
            BriefPipeline.Sections.Count, null, null, null);
    }

    public RunStatus Status => _status;

    public UsageInfo Usage()
    {
        var month = AiUsageRepository.MonthKey(DateTimeOffset.Now);
        var provider = svc.Settings.AiProvider ?? BriefProviderKinds.Disabled;
        var enabled = provider switch
        {
            BriefProviderKinds.Anthropic => !string.IsNullOrWhiteSpace(svc.Settings.AnthropicApiKey),
            BriefProviderKinds.OpenWebUi => !string.IsNullOrWhiteSpace(svc.Settings.OpenWebUiBaseUrl)
                                            && !string.IsNullOrWhiteSpace(svc.Settings.OpenWebUiApiKey)
                                            && !string.IsNullOrWhiteSpace(svc.Settings.OpenWebUiModel),
            _ => false,
        };
        // the hard monthly ceiling is an Anthropic-cost control; the local
        // server has no per-token cost, so usage is logged but never enforced
        return new UsageInfo(month, svc.AiUsage.GetTokensUsed(month),
            svc.Settings.AiMonthlyTokenBudget, enabled,
            provider, BudgetEnforced: provider == BriefProviderKinds.Anthropic);
    }

    /// Kicks off generation in the background (the pipeline can legitimately
    /// run minutes on a local model). Returns the stored brief immediately on
    /// an idempotency hit; null means "started — poll Status".
    public async Task<ResearchBriefRow?> RequestGenerationAsync(string ticker, bool force,
        CancellationToken ct)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        EnsureConfigured();

        var (payload, payloadJson, hash) = await BuildPayloadAsync(ticker, ct);
        if (!force && svc.Briefs.FindBySnapshotHash(ticker, hash) is { } existing)
            return existing;

        if (_status.Active)
            throw new BriefGenerationException(
                $"A brief for {_status.Ticker} is already generating — briefs run one at a time.");

        var t = ticker;
        _status = new RunStatus(true, t, "starting", 0, BriefPipeline.Sections.Count,
            DateTimeOffset.Now, null, null);
        _ = Task.Run(async () =>
        {
            try
            {
                var brief = await GenerateCoreAsync(t, payload, payloadJson, hash, CancellationToken.None);
                _status = RunStatus.Idle with { LastBriefId = brief.Id };
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "brief generation for {Ticker} failed", t);
                _status = RunStatus.Idle with { LastError = ex.Message };
            }
        });
        return null;
    }

    /// Synchronous path used by the weekly batch (already background) — same
    /// idempotency and sequencing as the interactive path.
    public async Task<ResearchBriefRow> GenerateAsync(string ticker, CancellationToken ct)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        EnsureConfigured();
        var (payload, payloadJson, hash) = await BuildPayloadAsync(ticker, ct);
        if (svc.Briefs.FindBySnapshotHash(ticker, hash) is { } existing) return existing;
        return await GenerateCoreAsync(ticker, payload, payloadJson, hash, ct);
    }

    private async Task<ResearchBriefRow> GenerateCoreAsync(string ticker, BriefPayload payload,
        string payloadJson, string hash, CancellationToken ct)
    {
        var provider = svc.ResolveBriefProvider()
            ?? throw new BriefGenerationException("AI briefs are disabled — choose a provider in Settings.");

        await _sequential.WaitAsync(ct);
        try
        {
            // resume: a previously interrupted run left passing sections in
            // the trace for this exact snapshot — reuse instead of re-calling
            var priorPassing = svc.BriefTraces.GetUnlinked(ticker, hash)
                .Where(t => t.Passed)
                .GroupBy(t => t.Section)
                .ToDictionary(g => g.Key, g => g.Last().CleanedText);

            // overall budget covers every section's calls at the configured
            // per-call timeout, so a slow local model is never cut off between
            // sections; still bounded so a hung server can't run forever
            var perCall = TimeSpan.FromSeconds(Math.Clamp(svc.Settings.OpenWebUiTimeoutSeconds, 10, 3600));
            var budget = perCall * (BriefPipeline.MaxCalls + 1) + TimeSpan.FromMinutes(2);
            var pipeline = new BriefPipeline(provider, svc.BriefPrompts, budget);
            BriefPipelineResult result;
            try
            {
                result = await pipeline.RunAsync(payload,
                    resume: key => priorPassing.GetValueOrDefault(key),
                    onSection: (section, index, total) =>
                    {
                        PersistTrace(ticker, hash, section);
                        _status = _status with
                        {
                            Stage = section.Title,
                            SectionIndex = Math.Min(index + 1, total),
                        };
                    },
                    ct);
            }
            catch (BriefSectionException ex)
            {
                // transport died mid-pipeline: keep the evidence, surface the error
                PersistTrace(ticker, hash, ex.Section);
                throw new BriefGenerationException(ex.Message, ex);
            }

            var row = new ResearchBriefRow
            {
                Ticker = ticker,
                CreatedAt = DateTimeOffset.Now,
                Model = svc.Settings.AiProvider == BriefProviderKinds.OpenWebUi
                    ? svc.Settings.OpenWebUiModel ?? "openwebui"
                    : svc.Settings.AnthropicModel,
                InputSnapshotJson = payloadJson,
                BriefMarkdown = result.Markdown,
                TokensUsed = result.TokensUsed,
                NeedsReview = result.NeedsReview,
                InputSnapshotHash = hash,
            };
            row.Id = svc.Briefs.Insert(row);
            svc.BriefTraces.LinkToBrief(ticker, hash, row.Id);

            if (result.TokensUsed > 0)
                svc.AiUsage.AddTokens(AiUsageRepository.MonthKey(row.CreatedAt), result.TokensUsed);
            log.LogInformation(
                "brief generated for {Ticker}: {Calls} calls, {Tokens} tokens, needsReview={NeedsReview}",
                ticker, result.CallsUsed, result.TokensUsed, result.NeedsReview);
            return row;
        }
        finally
        {
            _sequential.Release();
        }
    }

    private void PersistTrace(string ticker, string hash, SectionResult section)
    {
        if (section.Resumed) return;   // already on disk from the interrupted run
        foreach (var entry in section.Trace)
            svc.BriefTraces.Insert(new BriefTraceRow
            {
                Ticker = ticker,
                SnapshotHash = hash,
                Section = entry.Section,
                Stage = entry.Stage,
                UserPrompt = entry.UserPrompt,
                RawResponse = entry.RawResponse,
                CleanedText = entry.CleanedText,
                ViolationsJson = JsonSerializer.Serialize(entry.Violations, JsonOpts),
                Passed = entry.Passed,
                CreatedAt = DateTimeOffset.Now,
            });
    }

    /// What the Settings form is currently showing — so "Test connection"
    /// exercises the values on screen, saved or not. Null fields fall back to
    /// the saved settings.
    public record TestAiRequest(string? AiProvider, string? OpenWebUiBaseUrl,
        string? OpenWebUiApiKey, string? OpenWebUiModel,
        string? AnthropicApiKey, string? AnthropicModel,
        bool? OpenWebUiDisableThinking = null);

    // the provider's per-call timeout is the sole authority; this client must
    // never pre-empt it, so it has no timeout of its own
    private static readonly HttpClient TestHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// A 1-token ping so Settings can surface HTTP/SSL/auth problems readably
    /// — before the user commits the settings, not on the first real brief.
    public async Task<(bool Ok, string? Error)> TestConnectionAsync(TestAiRequest? req,
        CancellationToken ct)
    {
        try
        {
            var provider = BuildProvider(req)
                ?? throw new BriefGenerationException("AI briefs are disabled — choose a provider first.");
            // a small cap: the test only needs to prove reachability + auth +
            // a valid completion. A thinking model will spend the cap reasoning
            // and return empty content — accepted below as a working connection,
            // and fast, rather than waiting out a full generation.
            await provider.SendMessageAsync("Reply with the single word: ok", "ping", ct, maxTokens: 32);
            return (true, null);
        }
        catch (ModelEmptyResponseException)
        {
            // reached the server, authenticated, got a valid completion object —
            // the model just produced no final text under the test cap. The
            // connection itself works, which is what this button verifies.
            return (true, null);
        }
        catch (BriefGenerationException ex)
        {
            return (false, ex.Message);
        }
    }

    private IBriefModelProvider? BuildProvider(TestAiRequest? req)
    {
        if (req is null) return svc.ResolveBriefProvider();
        var kind = req.AiProvider?.Trim().ToLowerInvariant() ?? svc.Settings.AiProvider;
        return kind switch
        {
            BriefProviderKinds.OpenWebUi => new OpenWebUiBriefProvider(TestHttp,
                () => (req.OpenWebUiBaseUrl?.Trim().TrimEnd('/') ?? svc.Settings.OpenWebUiBaseUrl,
                       req.OpenWebUiApiKey?.Trim() ?? svc.Settings.OpenWebUiApiKey,
                       req.OpenWebUiModel?.Trim() ?? svc.Settings.OpenWebUiModel),
                () => TimeSpan.FromSeconds(Math.Clamp(svc.Settings.OpenWebUiTimeoutSeconds, 10, 3600)),
                () => req.OpenWebUiDisableThinking ?? svc.Settings.OpenWebUiDisableThinking),
            BriefProviderKinds.Anthropic => new AnthropicBriefProvider(
                new AnthropicClient(TestHttp,
                    () => req.AnthropicApiKey?.Trim() ?? svc.Settings.AnthropicApiKey),
                () => string.IsNullOrWhiteSpace(req.AnthropicModel)
                    ? svc.Settings.AnthropicModel : req.AnthropicModel.Trim()),
            _ => null,
        };
    }

    private void EnsureConfigured()
    {
        var usage = Usage();
        if (usage.Provider == BriefProviderKinds.Disabled)
            throw new BriefGenerationException("AI briefs are disabled — choose a provider in Settings.");
        if (!usage.Enabled)
            throw new BriefGenerationException(usage.Provider == BriefProviderKinds.Anthropic
                ? "AI briefs are off until an Anthropic API key is set in Settings."
                : "AI briefs are off until the local server's URL, API key, and model are set in Settings.");
        if (usage.BudgetEnforced && usage.TokensUsed >= usage.Budget)
            throw new BriefGenerationException(
                $"Monthly AI token budget reached ({usage.TokensUsed:N0} of {usage.Budget:N0}). " +
                "Raise the budget in Settings or wait for next month.");
    }

    private async Task<(BriefPayload Payload, string Json, string Hash)> BuildPayloadAsync(
        string ticker, CancellationToken ct)
    {
        var payload = await AssemblePayloadAsync(ticker, ct);
        var json = BriefPayloadBuilder.Serialize(payload);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return (payload, json, hash);
    }

    private async Task<BriefPayload> AssemblePayloadAsync(string ticker, CancellationToken ct)
    {
        // evidence exactly as the queue shows it — held candidates included so
        // a brief can be requested from the research page too
        var built = queue.Build(includeHeld: true);
        var candidate = built.Candidates.FirstOrDefault(c =>
            string.Equals(c.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
        var evidence = candidate?.Evidence ?? new List<EvidenceItem>();
        var sector = candidate?.Sector ?? built.SectorByTicker.GetValueOrDefault(ticker);

        // fetch-through (not cache-only): search-driven briefs cover tickers
        // the research page has never opened
        var profile = await DegradeAsync(ticker, "profile",
            c => svc.Research.GetProfileAsync(ticker, c), ct);
        var description = await FetchDescriptionAsync(ticker, ct);
        // live metrics are always fetched (server-cached): they carry the
        // leverage/dividend lines the warehouse lacks, and for non-universe
        // tickers they stand in for the whole warehouse row
        var liveMetrics = await DegradeAsync(ticker, "fundamentals",
            c => svc.Research.GetMetricsAsync(ticker, c), ct);
        var metrics = svc.Metrics.GetAll()
            .FirstOrDefault(m => string.Equals(m.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
        if (metrics is null && liveMetrics is { } fm)
            metrics = new TickerMetricsRow
            {
                Ticker = ticker, AsOf = DateTimeOffset.Now,
                MarketCap = fm.MarketCap, Pe = fm.PeTtm, Ps = fm.Ps, Pb = fm.Pb,
                RevenueGrowthYoY = fm.RevenueGrowthYoY, EpsGrowth = fm.EpsGrowthYoY,
                GrossMargin = fm.GrossMargin, NetMargin = fm.NetMargin, Roe = fm.Roe,
                Beta = fm.Beta, Wk52High = fm.High52, Wk52Low = fm.Low52,
            };
        var nextEarnings = await DegradeAsync(ticker, "next earnings date",
            c => svc.Research.GetNextEarningsDateAsync(ticker, c), ct);

        SectorSummary? sectorSummary = null;
        if (sector is not null && sector != SectorAnalyzer.Unclassified)
        {
            var groups = Endpoints.SectorEndpoints.BuildGroups(svc);
            if (groups.TryGetValue(sector, out var rows))
                sectorSummary = SectorAnalyzer.Summarize(sector, rows);
        }

        var signals = svc.SignalHits.GetForTicker(ticker)
            .Select(h => (Hit: h, Why: ParseWhy(h.InputsJson)))
            .Where(s => s.Why is not null)
            .Select(s => (s.Hit, Why: s.Why!))
            .ToList();

        var fitLine = queue.FitLine(ticker, sector, built.PortfolioSectorWeights,
            built.FitByTicker, built.Settings);

        // provider-backed context (news, quote, analyst recs, earnings
        // history): each degrades independently to "absent" on a fetch
        // failure, it never blocks the brief
        var news = await DegradeAsync(ticker, "news",
            c => svc.Research.GetNewsAsync(ticker, c), ct) ?? new List<NewsItem>();
        var quote = await DegradeAsync(ticker, "quote",
            c => svc.Research.GetQuoteDetailAsync(ticker, c), ct);
        var recommendations = await DegradeAsync(ticker, "recommendations",
            c => svc.Research.GetRecommendationsAsync(ticker, c), ct);
        var earnings = await DegradeAsync(ticker, "earnings",
            c => svc.Research.GetEarningsAsync(ticker, c), ct);

        // the Buy score's primary inputs: the stock's own two-month return
        // record (warehouse closes, falling back to fetched history for
        // non-universe tickers), and the 60d scoreboard for the fired kinds
        var returnHistory = ReturnHistoryMath.TwoMonth(svc.DailyCloses.GetForTicker(ticker))
            ?? ReturnHistoryMath.TwoMonth(await FetchHistoryClosesAsync(ticker, ct));
        List<ScoreboardRow>? signalRecords = null;
        var firedKinds = signals.Select(s => s.Hit.SignalKind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (firedKinds.Count > 0)
            signalRecords = ScoreboardMath
                .Build(svc.SignalHits.GetAll(includeDismissed: true),
                    svc.SignalOutcomes.GetAll(), downWeightingEnabled: false)
                .Where(r => firedKinds.Contains(r.SignalKind))
                .ToList();

        return BriefPayloadBuilder.Build(ticker, profile, sector, evidence, metrics,
            sectorSummary, signals, fitLine, news, quote, recommendations, earnings,
            returnHistory, signalRecords, description, liveMetrics, nextEarnings);
    }

    /// Yahoo business summary for the deterministic company header, cached a
    /// month (descriptions don't move). A blank result is cached too, so an
    /// unknown symbol doesn't re-run the cookie dance every brief.
    private async Task<string?> FetchDescriptionAsync(string ticker, CancellationToken ct)
    {
        if (svc.ResearchCache.Get(ticker, "description", TimeSpan.FromDays(30)) is { } cached)
            return cached.Length == 0 ? null : cached;
        try
        {
            var text = await svc.CompanyDescriptions.GetBusinessSummaryAsync(ticker, ct);
            svc.ResearchCache.Put(ticker, "description", text ?? "", DateTimeOffset.Now);
            return text;
        }
        catch (PriceFetchException ex)
        {
            log.LogWarning(ex, "description unavailable for {Ticker} brief", ticker);
            return null;
        }
    }

    private async Task<T?> DegradeAsync<T>(string ticker, string what,
        Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
    {
        try
        {
            return await fetch(ct);
        }
        catch (PriceFetchException ex)
        {
            log.LogWarning(ex, "{What} unavailable for {Ticker} brief", what, ticker);
            return default;
        }
    }

    /// Daily closes for tickers outside the crawled universe: the research
    /// page's cached history first, then a Yahoo fetch (cached back for the
    /// chart to reuse). Capped to ~2 years so the track record matches the
    /// warehouse depth universe tickers get. Empty on failure — the score
    /// section then says the history is missing.
    private async Task<List<DailyCloseRow>> FetchHistoryClosesAsync(string ticker,
        CancellationToken ct)
    {
        // the history cache uses the endpoint's default (PascalCase)
        // serialization, not this service's web options — keep them matched
        List<DailyBar>? bars = null;
        if (svc.ResearchCache.Get(ticker, "history", TimeSpan.FromHours(24)) is { } cached)
        {
            try { bars = JsonSerializer.Deserialize<Endpoints.ResearchEndpoints.HistoryValue>(cached)?.Bars; }
            catch (JsonException) { /* stale shape: refetch below */ }
        }
        if (bars is null)
        {
            try
            {
                using var _ = svc.Interactive.Begin();   // the crawler shares Yahoo
                bars = await svc.History.GetDailyHistoryAsync(ticker, new DateOnly(1970, 1, 1), ct);
                svc.ResearchCache.Put(ticker, "history",
                    JsonSerializer.Serialize(new Endpoints.ResearchEndpoints.HistoryValue(bars)),
                    DateTimeOffset.Now);
            }
            catch (PriceFetchException ex)
            {
                log.LogWarning(ex, "history unavailable for {Ticker} brief", ticker);
                return new List<DailyCloseRow>();
            }
        }
        var cutoff = DateOnly.FromDateTime(DateTime.Today).AddDays(-730);
        return bars
            .Where(b => b.Date >= cutoff)
            .OrderBy(b => b.Date)
            .Select(b => new DailyCloseRow { Ticker = ticker, Date = b.Date, Close = b.Close })
            .ToList();
    }

    private static SignalWhy? ParseWhy(string json)
    {
        try { return JsonSerializer.Deserialize<SignalWhy>(json, JsonOpts); }
        catch (JsonException) { return null; }
    }
}
