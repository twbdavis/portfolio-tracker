namespace PortfolioTracker.Core.Research;

/// The Anthropic path, kept behind the same provider seam so switching
/// AI_VISION_PROVIDER between "openwebui" and "anthropic" changes transport
/// only — the BriefAgent pipeline, grounding checks, and storage are
/// identical either way.
public class AnthropicBriefProvider(AnthropicClient client, Func<string> model) : IBriefModelProvider
{
    public async Task<ModelResult> SendMessageAsync(string systemPrompt, string userPrompt,
        CancellationToken ct, int? maxTokens = null)
    {
        var m = model();
        var completion = await client.CompleteAsync(m, systemPrompt, userPrompt, ct).ConfigureAwait(false);
        // Claude doesn't emit think tags, but strip defensively so the
        // pipeline's cleaned-text contract holds for every provider
        return new ModelResult(ThinkTagStripper.Strip(completion.Text), completion.Text,
            completion.TotalTokens, m);
    }
}
