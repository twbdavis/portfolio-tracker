using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PortfolioTracker.Core.Research;

/// OpenAI-compatible chat completions against a local OpenWebUI server.
/// Local-model realities live here: think-tag stripping, a hard 60s per-call
/// timeout, and readable errors for the unreachable-server case (VPN off,
/// wrong network) so the UI can say "Local AI server unreachable" instead of
/// crashing a page. Callers (BriefAgent) guarantee strictly sequential use —
/// this talks to shared company hardware.
public class OpenWebUiBriefProvider(HttpClient http,
    Func<(string? BaseUrl, string? ApiKey, string? Model)> config,
    Func<TimeSpan>? callTimeout = null,
    Func<bool>? disableThinking = null) : IBriefModelProvider
{
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(300);
    /// Sections are ~200 tokens; this cap only applies with thinking disabled,
    /// where it turns a runaway generation into a fast failure instead of a
    /// per-call-timeout wait. With thinking on it would truncate mid-reasoning.
    public const int NoThinkMaxTokens = 2000;
    private readonly Func<TimeSpan> _callTimeout = callTimeout ?? (() => DefaultCallTimeout);
    private readonly Func<bool> _disableThinking = disableThinking ?? (() => false);

    public async Task<ModelResult> SendMessageAsync(string systemPrompt, string userPrompt,
        CancellationToken ct, int? maxTokens = null)
    {
        var (baseUrl, apiKey, model) = config();
        var noThink = _disableThinking();
        if (noThink) maxTokens ??= NoThinkMaxTokens;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new BriefGenerationException("OPENWEBUI_BASE_URL is not set. Open Settings to configure the local AI server.");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new BriefGenerationException("OPENWEBUI_API_KEY is not set. Open Settings to configure the local AI server.");
        if (string.IsNullOrWhiteSpace(model))
            throw new BriefGenerationException("OPENWEBUI_MODEL is not set. Open Settings to configure the local AI server.");

        var url = $"{baseUrl.TrimEnd('/')}/api/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = Messages(systemPrompt, userPrompt),
            ["stream"] = false,
        };
        // suppresses qwen chain-of-thought at the chat-template level (vLLM /
        // SGLang style). Verified against this feature's target server: the
        // qwen /no_think prompt switch is IGNORED there (the model reasons
        // straight through it), while this kwarg drops a 15s deliberation to
        // sub-second. Backends that don't know the field ignore it.
        if (noThink)
            payload["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false };
        if (maxTokens is int mt)
            payload["max_tokens"] = mt;
        request.Content = JsonContent.Create(payload);

        var callTimeout = _callTimeout();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(callTimeout);
        try
        {
            using var resp = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BriefGenerationException(
                    $"Local AI server rejected the API key ({(int)resp.StatusCode}). Check OPENWEBUI_API_KEY in Settings.");
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new BriefGenerationException(
                    $"Local AI server returned {(int)resp.StatusCode}: {Truncate(body, 300)}");
            }
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return Parse(doc, model);
        }
        catch (HttpRequestException ex)
        {
            // DNS failure / connection refused / SSL — the VPN-off case
            throw new BriefGenerationException(
                $"Local AI server unreachable ({ex.InnerException?.Message ?? ex.Message}). " +
                "Check your network/VPN and OPENWEBUI_BASE_URL.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelTimeoutException(
                $"Local AI server timed out after {callTimeout.TotalSeconds:F0}s. " +
                "Raise the per-call timeout in Settings if your model needs longer.");
        }
        catch (JsonException ex)
        {
            throw new BriefGenerationException($"Unreadable response from local AI server: {ex.Message}", ex);
        }
    }

    private static object[] Messages(string systemPrompt, string userPrompt) => new object[]
    {
        new { role = "system", content = systemPrompt },
        new { role = "user", content = userPrompt },
    };

    private static ModelResult Parse(JsonDocument doc, string model)
    {
        var root = doc.RootElement;
        string? content = null, reasoning = null, finishReason = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finishReason = fr.GetString();
            if (first.TryGetProperty("message", out var message))
            {
                content = Str(message, "content");
                // reasoning models (qwen3, DeepSeek-R1) route chain-of-thought
                // into reasoning_content and leave content empty when they only
                // think or get truncated — capture it for the trace and errors
                reasoning = Str(message, "reasoning_content") ?? Str(message, "reasoning");
            }
        }

        // usage is optional on local servers — tolerate absence, log when present
        int? tokens = null;
        if (root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("total_tokens", out var total)
            && total.ValueKind == JsonValueKind.Number)
            tokens = total.GetInt32();

        var cleaned = string.IsNullOrWhiteSpace(content) ? "" : ThinkTagStripper.Strip(content);
        if (cleaned.Length > 0)
        {
            // servers that route chain-of-thought into reasoning_content would
            // otherwise hide it from the trace entirely — fold it back in as a
            // think block so "how long did the model deliberate" is diagnosable
            var raw = string.IsNullOrWhiteSpace(reasoning)
                ? content!
                : $"<think>\n{reasoning}\n</think>\n{content}";
            return new ModelResult(cleaned, raw, tokens, model);
        }

        // content empty (or only reasoning): reachable + authenticated, but no
        // usable answer. A tight max_tokens on a thinking model is the usual
        // cause; surface that so the connection test can accept it and a real
        // brief can report it clearly.
        var truncated = finishReason == "length";
        var reasoningOnly = !string.IsNullOrWhiteSpace(reasoning);
        var detail = truncated
            ? "the model hit its token limit before answering"
            : reasoningOnly
                ? "the model returned only reasoning (a thinking model like qwen3), no final text"
                : "the model returned an empty message";
        throw new ModelEmptyResponseException(
            $"Local AI server responded but produced no usable text — {detail}. " +
            "For thinking models, this is expected on the short connection-test ping.");
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
