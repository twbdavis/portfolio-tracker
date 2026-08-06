using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortfolioTracker.Core.Research;

public record BriefSectionSpec(string Key, string Title, string TemplateFile);

/// One model interaction inside a brief: what was sent, what came back raw,
/// what survived cleaning, and how the grounding check judged it.
public record BriefTraceEntry(string Section, string Stage, string UserPrompt,
    string RawResponse, string CleanedText, List<GroundingViolation> Violations, bool Passed);

public record SectionResult(string Key, string Title, string Markdown, bool Passed,
    bool Resumed, List<BriefTraceEntry> Trace, int TokensUsed);

public record BriefPipelineResult(string Markdown, bool NeedsReview, int CallsUsed,
    int TokensUsed, List<SectionResult> Sections, string? AbortReason);

/// Per-section data slices from the whitelisted payload. Smaller context per
/// call means better instruction adherence on a small local model — and the
/// grounding checker verifies each section against exactly what it saw.
public static class BriefSlices
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    public static string For(string sectionKey, BriefPayload payload)
    {
        object slice = sectionKey switch
        {
            "key-numbers" => new { payload.Ticker, payload.Price, payload.Fundamentals },
            "bear-case" => new
            {
                payload.Ticker,
                payload.Price,
                payload.Fundamentals,
                payload.SectorHealth,
                payload.AnalystRecommendations,
                payload.EarningsHistory,
                FailedSignalConditions = NullIfEmpty((payload.Signals ?? new())
                    .Select(s => new
                    {
                        s.Kind,
                        Failed = s.Conditions.Where(c => !c.Pass).ToList(),
                    })
                    .Where(s => s.Failed.Count > 0)
                    .ToList()),
                SectorHealthEvidence = NullIfEmpty((payload.Evidence ?? new())
                    .Where(e => e.Dimension == QueueDimensions.SectorHealth)
                    .ToList()),
                FailedEvidence = NullIfEmpty(
                    (payload.Evidence ?? new()).Where(e => e.Status == "Fail").ToList()),
            },
            "questions" => payload,
            // the score weighs everything, so it sees everything — with the
            // two-month track record as its named primary input
            "score" => payload,
            _ => throw new BriefGenerationException($"Unknown brief section \"{sectionKey}\"."),
        };
        return JsonSerializer.Serialize(slice, JsonOpts);
    }

    private static List<T>? NullIfEmpty<T>(List<T> list) => list.Count == 0 ? null : list;
}

/// The BriefAgent: sequential sectioned generation for small local models.
/// Each section gets its own call with only its data slice, a programmatic
/// grounding check, and at most ONE repair call. A section that still fails
/// keeps its text — withholding punished legitimate aggregation ("22 of 24
/// analysts") more often than hallucination — but the unverified numbers are
/// listed in a visible footnote and the brief carries a NeedsReview flag.
/// Unverified numbers are never stored silently.
public class BriefPipeline(IBriefModelProvider provider, BriefPromptTemplates templates,
    TimeSpan? maxDuration = null)
{
    // four sections plus room for up to two repair calls
    public const int MaxCalls = 6;
    /// Default overall budget when a caller doesn't scale it to the model's
    /// per-call timeout. Generous so a slow local model isn't cut off mid-brief.
    public static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromMinutes(60);
    private readonly TimeSpan _maxDuration = maxDuration ?? DefaultMaxDuration;
    public const string DataPlaceholder = "{{DATA}}";

    // Insight-first order: the score (which sees the full payload and names
    // the central tension) leads; supporting detail follows. The company
    // header is rendered deterministically — no model call can improve five
    // profile fields, and the old overview section only ever paraphrased
    // them. ("Why it surfaced" was removed 2026-08-05: the queue's evidence
    // panel already shows that data; it still feeds bear-case/questions.)
    public static readonly IReadOnlyList<BriefSectionSpec> Sections = new[]
    {
        new BriefSectionSpec("score", "Buy score", "section-score.md"),
        new BriefSectionSpec("key-numbers", "The 3 numbers that matter most", "section-key-numbers.md"),
        new BriefSectionSpec("bear-case", "Bear case / key risks", "section-bear-case.md"),
        new BriefSectionSpec("questions", "Questions to answer before investing", "section-questions.md"),
    };

    public const string FailureNotice =
        "> ⚠ This section failed grounding checks and was withheld. " +
        "Open the generation trace for the model's attempts and the specific violations.";

    /// resume: sectionKey -> previously stored PASSING markdown for the same
    /// input snapshot, so an interrupted brief doesn't re-pay completed calls.
    /// onSection fires after each section settles (for incremental persistence
    /// and UI progress). Calls run strictly sequentially by construction.
    public async Task<BriefPipelineResult> RunAsync(BriefPayload payload,
        Func<string, string?>? resume = null,
        Action<SectionResult, int, int>? onSection = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var systemPrompt = templates.Load(BriefPromptTemplates.SystemFile);
        var sections = new List<SectionResult>();
        var callsUsed = 0;
        var tokensUsed = 0;
        string? abortReason = null;

        for (var i = 0; i < Sections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var spec = Sections[i];

            if (resume?.Invoke(spec.Key) is string stored)
            {
                var resumed = new SectionResult(spec.Key, spec.Title, stored, Passed: true,
                    Resumed: true, new List<BriefTraceEntry>(), TokensUsed: 0);
                sections.Add(resumed);
                onSection?.Invoke(resumed, i, Sections.Count);
                continue;
            }

            abortReason ??= BudgetExceeded(callsUsed, stopwatch.Elapsed, _maxDuration);
            if (abortReason is not null)
            {
                var aborted = new SectionResult(spec.Key, spec.Title, FailureNotice, Passed: false,
                    Resumed: false, new List<BriefTraceEntry>
                    {
                        new(spec.Key, "aborted", "", "", "",
                            new List<GroundingViolation>
                                { new("budget", abortReason) }, Passed: false),
                    }, TokensUsed: 0);
                sections.Add(aborted);
                onSection?.Invoke(aborted, i, Sections.Count);
                continue;
            }

            var result = await GenerateSectionAsync(spec, payload, systemPrompt,
                MaxCalls - callsUsed, ct);
            callsUsed += result.Trace.Count(t => t.Stage is "generate" or "repair");
            tokensUsed += result.TokensUsed;
            sections.Add(result);
            onSection?.Invoke(result, i, Sections.Count);
        }

        var needsReview = sections.Any(s => !s.Passed);
        var markdown = new StringBuilder();
        markdown.Append(RenderCompanyHeader(payload));
        foreach (var s in sections)
            markdown.Append("## ").Append(s.Title).Append("\n\n").Append(s.Markdown.Trim()).Append("\n\n");

        return new BriefPipelineResult(markdown.ToString().TrimEnd() + "\n", needsReview,
            callsUsed, tokensUsed, sections, abortReason);
    }

    /// The "What the company does" block, rendered straight from the payload
    /// — five profile fields plus the fetched business summary need no
    /// paraphrasing, and the model version of this section only ever droned.
    public static string RenderCompanyHeader(BriefPayload payload)
    {
        var p = payload.Profile;
        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p?.Sector)) facts.Add(p!.Sector!);
        if (!string.IsNullOrWhiteSpace(p?.Industry)
            && !string.Equals(p!.Industry, p.Sector, StringComparison.OrdinalIgnoreCase))
            facts.Add(p.Industry!);
        if (p?.MarketCapMillions is decimal mc && mc > 0m)
            facts.Add(mc >= 1000m ? $"${mc / 1000m:0.#}B market cap" : $"${mc:0}M market cap");
        if (p?.IpoDate is { Length: >= 4 } ipo) facts.Add($"IPO {ipo[..4]}");

        var line = $"**{(string.IsNullOrWhiteSpace(p?.Name) ? payload.Ticker : p!.Name)}** ({payload.Ticker})";
        if (facts.Count > 0) line += " · " + string.Join(" · ", facts);

        var sb = new StringBuilder();
        sb.Append("## What the company does\n\n").Append(line).Append('\n');
        if (!string.IsNullOrWhiteSpace(p?.Description))
            sb.Append('\n').Append(p!.Description).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    private async Task<SectionResult> GenerateSectionAsync(BriefSectionSpec spec,
        BriefPayload payload, string systemPrompt, int callsBudgetLeft, CancellationToken ct)
    {
        var template = templates.Load(spec.TemplateFile);
        var slice = BriefSlices.For(spec.Key, payload);
        var trace = new List<BriefTraceEntry>();
        var tokens = 0;

        var prompt = WithData(template, slice);
        ModelResult generated;
        try
        {
            generated = await SendWithOneRetryAsync(systemPrompt, prompt, spec.Key, "generate", trace, ct);
        }
        catch (BriefGenerationException ex)
        {
            trace.Add(new BriefTraceEntry(spec.Key, "generate", prompt, ex.Message, "",
                new List<GroundingViolation> { new("transport", ex.Message) }, Passed: false));
            // a transport failure here fails the whole brief — rethrow after
            // recording the trace so the caller can persist what happened
            throw new BriefSectionException(spec.Key,
                new SectionResult(spec.Key, spec.Title, FailureNotice, false, false, trace, tokens), ex);
        }
        tokens += generated.TokensUsed ?? 0;

        var check = GroundingChecker.Check(generated.CleanedText, slice, template);
        trace.Add(new BriefTraceEntry(spec.Key, "generate", prompt, generated.RawResponse,
            generated.CleanedText, check.Violations, check.Passed));
        if (check.Passed)
            return new SectionResult(spec.Key, spec.Title, generated.CleanedText, true, false, trace, tokens);

        // no budget for a repair call: keep the text, flag the numbers
        if (callsBudgetLeft < 2)
            return new SectionResult(spec.Key, spec.Title,
                WithUnverifiedNote(generated.CleanedText, check.Violations),
                false, false, trace, tokens);

        // exactly one repair attempt, with the violations spelled out and the
        // same data slice in reach
        var repairTemplate = templates.Load(BriefPromptTemplates.RepairFile);
        var repairPrompt = WithData(repairTemplate
                .Replace("{{VIOLATIONS}}", string.Join("\n", check.Violations.Select(v => $"- {v.Detail}")))
                .Replace("{{ORIGINAL}}", generated.CleanedText),
            slice);
        ModelResult repaired;
        try
        {
            repaired = await SendWithOneRetryAsync(systemPrompt, repairPrompt, spec.Key, "repair", trace, ct);
        }
        catch (BriefGenerationException ex)
        {
            trace.Add(new BriefTraceEntry(spec.Key, "repair", repairPrompt, ex.Message, "",
                new List<GroundingViolation> { new("transport", ex.Message) }, Passed: false));
            throw new BriefSectionException(spec.Key,
                new SectionResult(spec.Key, spec.Title, FailureNotice, false, false, trace, tokens), ex);
        }
        tokens += repaired.TokensUsed ?? 0;

        var recheck = GroundingChecker.Check(repaired.CleanedText, slice, template);
        trace.Add(new BriefTraceEntry(spec.Key, "repair", repairPrompt, repaired.RawResponse,
            repaired.CleanedText, recheck.Violations, recheck.Passed));
        return recheck.Passed
            ? new SectionResult(spec.Key, spec.Title, repaired.CleanedText, true, false, trace, tokens)
            : new SectionResult(spec.Key, spec.Title,
                WithUnverifiedNote(repaired.CleanedText, recheck.Violations),
                false, false, trace, tokens);
    }

    /// Failed-but-kept sections carry their unverified numbers in the open:
    /// the reader sees exactly which figures to double-check instead of a
    /// withheld section.
    private static string WithUnverifiedNote(string text, List<GroundingViolation> violations)
    {
        var tokens = violations
            .Where(v => v.Kind == GroundingViolation.Number)
            .Select(v => v.Token ?? v.Detail)
            .Distinct()
            .ToList();
        if (tokens.Count == 0) return text;
        return text.TrimEnd() +
               $"\n\n> ⚠ Could not verify against the input data: {string.Join(", ", tokens)}" +
               " — double-check these figures before relying on them (see the generation trace).";
    }

    /// One automatic retry on a per-call timeout: a shared local server can
    /// stall on one request and answer the next normally. The failed attempt
    /// is recorded in the trace under its real stage, so it counts against the
    /// call budget like any other call. Any other failure propagates as before.
    private async Task<ModelResult> SendWithOneRetryAsync(string systemPrompt, string prompt,
        string sectionKey, string stage, List<BriefTraceEntry> trace, CancellationToken ct)
    {
        try
        {
            return await provider.SendMessageAsync(systemPrompt, prompt, ct);
        }
        catch (ModelTimeoutException ex)
        {
            trace.Add(new BriefTraceEntry(sectionKey, stage, prompt, ex.Message, "",
                new List<GroundingViolation>
                    { new("transport", $"{ex.Message} Retried once automatically.") }, Passed: false));
            return await provider.SendMessageAsync(systemPrompt, prompt, ct);
        }
    }

    private static string WithData(string template, string slice) =>
        template.Contains(DataPlaceholder)
            ? template.Replace(DataPlaceholder, slice)
            : $"{template.TrimEnd()}\n\nDATA:\n```json\n{slice}\n```";

    private static string? BudgetExceeded(int callsUsed, TimeSpan elapsed, TimeSpan maxDuration)
    {
        if (callsUsed >= MaxCalls)
            return $"call budget reached ({callsUsed} of {MaxCalls} model calls used)";
        if (elapsed >= maxDuration)
            return $"time budget reached ({elapsed.TotalMinutes:F1} of {maxDuration.TotalMinutes:F0} minutes)";
        return null;
    }
}

/// Carries the partial section (with its trace) out of a transport failure so
/// the service can persist the evidence before surfacing the error.
public class BriefSectionException(string sectionKey, SectionResult section, Exception inner)
    : Exception(inner.Message, inner)
{
    public string SectionKey { get; } = sectionKey;
    public SectionResult Section { get; } = section;
}
