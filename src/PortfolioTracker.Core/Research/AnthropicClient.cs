using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PortfolioTracker.Core.Research;

public class BriefGenerationException(string message, Exception? inner = null)
    : Exception(message, inner);

public record BriefCompletion(string Text, int InputTokens, int OutputTokens)
{
    public int TotalTokens => InputTokens + OutputTokens;
}

/// Minimal Anthropic Messages API client for research briefs. Same shape as
/// the Finnhub providers: HttpClient plus a key thunk, exceptions typed so
/// callers can surface errors without crashing. Retries are capped at one,
/// and only for transient failures (429/5xx/timeout).
public class AnthropicClient(HttpClient http, Func<string?> apiKey)
{
    public const string DefaultModel = "claude-sonnet-4-6";
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const int MaxOutputTokens = 2048;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    public async Task<BriefCompletion> CompleteAsync(string model, string systemPrompt,
        string userContent, CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new BriefGenerationException("Anthropic API key not set. Open Settings to add one.");

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendAsync(key, model, systemPrompt, userContent, ct).ConfigureAwait(false);
            }
            catch (BriefGenerationException ex) when (attempt == 0 && ex.Data.Contains("transient"))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<BriefCompletion> SendAsync(string key, string model, string systemPrompt,
        string userContent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model,
            max_tokens = MaxOutputTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userContent } },
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var resp = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new BriefGenerationException("Anthropic rejected the API key (401). Check Settings.");
            if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                throw Transient($"Anthropic returned {(int)resp.StatusCode}.");
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new BriefGenerationException(
                    $"Anthropic returned {(int)resp.StatusCode}: {Truncate(body, 300)}");
            }

            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return Parse(doc);
        }
        catch (HttpRequestException ex)
        {
            throw Transient($"Network error calling Anthropic: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Transient("Anthropic request timed out.");
        }
        catch (JsonException ex)
        {
            throw new BriefGenerationException($"Unreadable Anthropic response: {ex.Message}", ex);
        }
    }

    private static BriefCompletion Parse(JsonDocument doc)
    {
        var root = doc.RootElement;
        var text = "";
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                    text += txt.GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new BriefGenerationException("Anthropic returned an empty response.");

        int input = 0, output = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number)
                input = i.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number)
                output = o.GetInt32();
        }
        return new BriefCompletion(text, input, output);
    }

    private static BriefGenerationException Transient(string message, Exception? inner = null)
    {
        var ex = new BriefGenerationException(message, inner);
        ex.Data["transient"] = true;
        return ex;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
