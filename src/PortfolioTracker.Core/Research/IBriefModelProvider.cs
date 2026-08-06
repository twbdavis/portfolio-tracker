using System.Text.RegularExpressions;

namespace PortfolioTracker.Core.Research;

public static class BriefProviderKinds
{
    public const string OpenWebUi = "openwebui";
    public const string Anthropic = "anthropic";
    public const string Disabled = "disabled";
}

/// The server answered — reachable, authenticated, valid completion — but the
/// usable text was empty (a reasoning model that only produced chain-of-thought
/// under a tight token cap, or a truncated response). Distinct from a transport
/// failure so a connection test can treat it as "connected" while real brief
/// generation still rejects it.
public class ModelEmptyResponseException(string message) : BriefGenerationException(message);

/// One call exceeded the configured per-call timeout. Distinct from other
/// transport failures because it is worth exactly one automatic retry — a
/// shared local server can stall on one request and answer the next normally.
public class ModelTimeoutException(string message) : BriefGenerationException(message);

/// One model call's result. CleanedText has reasoning noise (think tags)
/// stripped and is what the pipeline consumes; RawResponse is preserved for
/// the generation trace, with out-of-band reasoning (reasoning_content)
/// folded back in as a think block. TokensUsed is null when the backend
/// doesn't report usage — local servers often don't.
public record ModelResult(string CleanedText, string RawResponse, int? TokensUsed, string Model);

/// The seam between the BriefAgent pipeline and whatever model serves it —
/// a local OpenWebUI instance or the Anthropic API. Implementations own their
/// transport quirks; the pipeline sees only (system, user) -> ModelResult.
public interface IBriefModelProvider
{
    /// maxTokens is a hint for cheap connection tests; providers may ignore it.
    Task<ModelResult> SendMessageAsync(string systemPrompt, string userPrompt,
        CancellationToken ct, int? maxTokens = null);
}

public static class ThinkTagStripper
{
    // matches an INNERMOST block (no nested opener inside); applied repeatedly
    // so nested/multiple blocks peel away from the inside out
    private static readonly Regex Block = new(@"<think>(?:(?!<think>).)*?</think>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Orphan = new(@"</?think>", RegexOptions.IgnoreCase);

    /// Removes qwen-style <think>...</think> reasoning blocks (multiple and
    /// nested) plus any orphaned tags, then trims.
    public static string Strip(string text)
    {
        var previous = text;
        while (true)
        {
            var next = Block.Replace(previous, "");
            if (next == previous) break;
            previous = next;
        }
        return Orphan.Replace(previous, "").Trim();
    }
}
