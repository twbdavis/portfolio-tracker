using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Tests;

public class BriefPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pt-pipeline-{Guid.NewGuid():N}");
    private readonly BriefPromptTemplates _templates;

    public BriefPipelineTests()
    {
        Directory.CreateDirectory(_dir);
        _templates = new BriefPromptTemplates(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// Scripted provider: answers from a queue, records every call. Grounded
    /// answers contain no numbers at all, so they always pass the checker.
    private sealed class FakeProvider : IBriefModelProvider
    {
        private readonly Queue<string> _responses = new();
        public List<(string System, string User)> Calls { get; } = new();
        public int MaxConcurrency { get; private set; }
        private int _inFlight;

        public FakeProvider(params string[] responses)
        {
            foreach (var r in responses) _responses.Enqueue(r);
        }

        public string DefaultResponse { get; set; } = "A grounded sentence with no figures.";

        public async Task<ModelResult> SendMessageAsync(string systemPrompt, string userPrompt,
            CancellationToken ct, int? maxTokens = null)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            MaxConcurrency = Math.Max(MaxConcurrency, inFlight);
            try
            {
                await Task.Yield();
                Calls.Add((systemPrompt, userPrompt));
                var text = _responses.Count > 0 ? _responses.Dequeue() : DefaultResponse;
                if (text == "!throw")
                    throw new BriefGenerationException("Local AI server unreachable (test).");
                if (text == "!timeout")
                    throw new ModelTimeoutException("Local AI server timed out after 1s (test).");
                return new ModelResult(ThinkTagStripper.Strip(text), text, 10, "fake-model");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private static BriefPayload Payload() => BriefPayloadBuilder.Build(
        "MU",
        new CompanyProfile { Ticker = "MU", Name = "Micron", Industry = "Semiconductors" },
        "Technology",
        new List<EvidenceItem>
        {
            new(QueueDimensions.Quality, "Quality", EvidenceStatus.Pass,
                "z = 1.2, 80th percentile", "≥ 67th percentile", "2026-07-31", 1m, 1m, null),
        },
        new TickerMetricsRow { Ticker = "MU", AsOf = DateTimeOffset.Now, Pe = 12.4m },
        null,
        Array.Empty<(SignalHit, SignalWhy)>(),
        "Low correlation (0.21) to your portfolio.",
        new List<NewsItem>());

    [Fact]
    public async Task Clean_run_makes_one_call_per_section_and_assembles_all_titles()
    {
        var provider = new FakeProvider();
        var result = await new BriefPipeline(provider, _templates).RunAsync(Payload());

        Assert.False(result.NeedsReview);
        Assert.Equal(BriefPipeline.Sections.Count, result.CallsUsed);
        Assert.Equal(1, provider.MaxConcurrency);   // strictly sequential
        foreach (var spec in BriefPipeline.Sections)
            Assert.Contains($"## {spec.Title}", result.Markdown);
    }

    [Fact]
    public async Task Failing_section_gets_one_repair_then_passes()
    {
        // first section invents a number, repair fixes it; everything else
        // clean. 47.9 collides with nothing in any template — 999.9 would
        // false-pass via the score template's "1 to 100" under the 0.001
        // unit factor (999.9 × 0.001 ≈ 1 at two significant figures).
        var provider = new FakeProvider("Revenue was 47.9% higher.", "A grounded sentence.");
        var result = await new BriefPipeline(provider, _templates).RunAsync(Payload());

        Assert.False(result.NeedsReview);
        Assert.Equal(BriefPipeline.Sections.Count + 1, result.CallsUsed);
        var first = result.Sections[0];
        Assert.True(first.Passed);
        Assert.Equal(new[] { "generate", "repair" }, first.Trace.Select(t => t.Stage));
        Assert.Contains("47.9", first.Trace[0].Violations.Single().Detail);
        Assert.Contains("47.9", first.Trace[1].UserPrompt);   // repair names the violation
        Assert.Contains("A grounded sentence.", result.Markdown);
    }

    [Fact]
    public async Task Repair_that_fails_again_withholds_the_section_and_flags_review()
    {
        var provider = new FakeProvider("Margin hit 47.9%.", "Margin truly hit 47.9%.");
        var result = await new BriefPipeline(provider, _templates).RunAsync(Payload());

        Assert.True(result.NeedsReview);
        var first = result.Sections[0];
        Assert.False(first.Passed);
        // the text is KEPT — withholding punished legitimate aggregation more
        // often than hallucination — but never silently: the unverified
        // number is called out in a footnote the reader can check
        Assert.Contains("Margin truly hit 47.9%.", first.Markdown);
        Assert.Contains("Could not verify against the input data: 47.9%", first.Markdown);
        Assert.All(result.Sections.Skip(1), s => Assert.True(s.Passed));
    }

    [Fact]
    public async Task Call_budget_aborts_remaining_sections_with_review_flag()
    {
        // every response is ungrounded: each section burns generate + repair
        var provider = new FakeProvider { DefaultResponse = "Untrue figure 47.9%." };
        var result = await new BriefPipeline(provider, _templates).RunAsync(Payload());

        Assert.True(result.NeedsReview);
        Assert.Equal(BriefPipeline.MaxCalls, result.CallsUsed);
        Assert.NotNull(result.AbortReason);
        Assert.Contains("call budget", result.AbortReason);
        // 6 calls = 3 sections tried; the fourth was aborted without a call
        Assert.Equal("aborted", result.Sections[^1].Trace.Single().Stage);
    }

    [Fact]
    public async Task Resume_skips_calls_for_sections_that_already_passed()
    {
        var provider = new FakeProvider();
        var stored = new Dictionary<string, string>
        {
            ["score"] = "Previously verified score text.",
            ["key-numbers"] = "Previously verified key-numbers text.",
        };
        var result = await new BriefPipeline(provider, _templates).RunAsync(Payload(),
            resume: key => stored.GetValueOrDefault(key));

        Assert.Equal(BriefPipeline.Sections.Count - 2, result.CallsUsed);
        Assert.True(result.Sections[0].Resumed);
        Assert.Contains("Previously verified score text.", result.Markdown);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public async Task Transport_failure_surfaces_with_the_partial_trace()
    {
        var provider = new FakeProvider("!throw");
        var ex = await Assert.ThrowsAsync<BriefSectionException>(() =>
            new BriefPipeline(provider, _templates).RunAsync(Payload()));
        Assert.Equal("score", ex.SectionKey);
        Assert.Contains("unreachable", ex.Message);
        Assert.Single(ex.Section.Trace);
    }

    [Fact]
    public async Task Timed_out_call_is_retried_once_and_recorded_in_the_trace()
    {
        var provider = new FakeProvider("!timeout");   // first call stalls, retry answers
        var result = await new BriefPipeline(provider, _templates).RunAsync(Payload());

        Assert.False(result.NeedsReview);
        var overview = result.Sections[0];
        Assert.True(overview.Passed);
        // the failed attempt stays in the trace under its real stage and
        // counts against the call budget like any other call
        Assert.Equal(new[] { "generate", "generate" }, overview.Trace.Select(t => t.Stage));
        Assert.Contains("Retried once", overview.Trace[0].Violations.Single().Detail);
        Assert.Equal(BriefPipeline.Sections.Count + 1, result.CallsUsed);
    }

    [Fact]
    public async Task Second_consecutive_timeout_fails_the_brief()
    {
        var provider = new FakeProvider("!timeout", "!timeout");
        var ex = await Assert.ThrowsAsync<BriefSectionException>(() =>
            new BriefPipeline(provider, _templates).RunAsync(Payload()));
        Assert.Equal("score", ex.SectionKey);
        Assert.Contains("timed out", ex.Message);
        // both attempts are in the carried-out trace for persistence
        Assert.Equal(2, ex.Section.Trace.Count);
    }

    [Fact]
    public async Task Sections_receive_sliced_data_not_the_full_payload()
    {
        var provider = new FakeProvider();
        await new BriefPipeline(provider, _templates).RunAsync(Payload());

        var scorePrompt = provider.Calls[0].User;
        Assert.Contains("Micron", scorePrompt);                    // the score sees everything
        Assert.Contains("80th percentile", scorePrompt);

        var keyNumbersPrompt = provider.Calls[1].User;
        Assert.Contains("12.4", keyNumbersPrompt);                 // fundamentals are in key-numbers
        Assert.DoesNotContain("Micron", keyNumbersPrompt);         // profile is not
        Assert.DoesNotContain("80th percentile", keyNumbersPrompt);
    }

    [Fact]
    public async Task Company_header_renders_deterministically_without_a_model_call()
    {
        var provider = new FakeProvider();
        var result = await new BriefPipeline(provider, _templates).RunAsync(Payload());

        Assert.Contains("## What the company does", result.Markdown);
        Assert.Contains("**Micron** (MU)", result.Markdown);
        Assert.Equal(BriefPipeline.Sections.Count, result.CallsUsed);   // header cost no call
    }

    [Fact]
    public async Task Think_tags_never_reach_the_stored_markdown()
    {
        var provider = new FakeProvider("<think>reasoning about numbers 42 77</think>Clean prose only.");
        var result = await new BriefPipeline(provider, _templates).RunAsync(Payload());
        Assert.Contains("Clean prose only.", result.Markdown);
        Assert.DoesNotContain("<think>", result.Markdown);
        Assert.False(result.NeedsReview);   // numbers inside think tags were stripped pre-check
    }
}
