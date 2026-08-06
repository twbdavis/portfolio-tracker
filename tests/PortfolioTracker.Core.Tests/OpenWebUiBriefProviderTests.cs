using System.Net;
using System.Text;
using PortfolioTracker.Core.Research;

namespace PortfolioTracker.Core.Tests;

public class OpenWebUiBriefProviderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((r, _) => Task.FromResult(respond(r))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return await respond(request, ct);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OpenWebUiBriefProvider Provider(FakeHandler handler, TimeSpan? timeout = null,
        bool disableThinking = false) =>
        new(new HttpClient(handler), () => ("https://ai.example.test", "KEY", "test-model"),
            timeout is TimeSpan t ? () => t : null,
            () => disableThinking);

    private static string Completion(string content, bool withUsage = true) =>
        $$"""
        {
          "choices": [{ "message": { "role": "assistant", "content": {{System.Text.Json.JsonSerializer.Serialize(content)}} } }]
          {{(withUsage ? """, "usage": { "prompt_tokens": 100, "completion_tokens": 50, "total_tokens": 150 }""" : "")}}
        }
        """;

    [Fact]
    public async Task Happy_path_posts_openai_shape_and_returns_cleaned_text()
    {
        var handler = new FakeHandler(_ => Json(Completion("The section body.")));
        var result = await Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None);

        Assert.Equal("The section body.", result.CleanedText);
        Assert.Equal(150, result.TokensUsed);
        Assert.Equal("test-model", result.Model);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ai.example.test/api/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("KEY", request.Headers.Authorization.Parameter);
        var body = handler.Bodies.Single();
        Assert.Contains("\"model\":\"test-model\"", body);
        Assert.Contains("\"role\":\"system\"", body);
        Assert.Contains("\"stream\":false", body);
    }

    [Fact]
    public async Task Think_tags_are_stripped_including_multiple_and_nested()
    {
        var raw = "<think>hmm let me plan</think>Answer part one. " +
                  "<think>outer <think>inner</think> more</think>And part two.";
        var handler = new FakeHandler(_ => Json(Completion(raw)));
        var result = await Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None);

        Assert.Equal("Answer part one. And part two.", result.CleanedText);
        Assert.Equal(raw, result.RawResponse);   // trace keeps the original
    }

    [Fact]
    public async Task Only_think_tags_is_a_distinct_empty_response_not_a_silent_section()
    {
        var handler = new FakeHandler(_ => Json(Completion("<think>all reasoning</think>")));
        await Assert.ThrowsAsync<ModelEmptyResponseException>(() =>
            Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None));
    }

    [Fact]
    public async Task Reasoning_only_response_with_empty_content_is_a_model_empty_response()
    {
        // qwen3 via Open WebUI: chain-of-thought in reasoning_content, content ""
        var body = """
            {
              "choices": [{
                "message": { "role": "assistant", "content": "", "reasoning_content": "let me think..." },
                "finish_reason": "length"
              }]
            }
            """;
        var handler = new FakeHandler(_ => Json(body));
        var ex = await Assert.ThrowsAsync<ModelEmptyResponseException>(() =>
            Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None));
        Assert.Contains("token limit", ex.Message);
    }

    [Fact]
    public async Task Reasoning_content_alongside_real_content_returns_the_content()
    {
        var body = """
            {
              "choices": [{
                "message": { "role": "assistant", "content": "The final answer.", "reasoning_content": "thinking" },
                "finish_reason": "stop"
              }]
            }
            """;
        var handler = new FakeHandler(_ => Json(body));
        var result = await Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None);
        Assert.Equal("The final answer.", result.CleanedText);
    }

    [Fact]
    public async Task Disable_thinking_sends_the_template_kwarg_and_caps_tokens()
    {
        var handler = new FakeHandler(_ => Json(Completion("body")));
        await Provider(handler, disableThinking: true)
            .SendMessageAsync("SYS", "USER", CancellationToken.None);

        var body = handler.Bodies.Single();
        Assert.Contains("\"chat_template_kwargs\":{\"enable_thinking\":false}", body);
        Assert.Contains($"\"max_tokens\":{OpenWebUiBriefProvider.NoThinkMaxTokens}", body);
    }

    [Fact]
    public async Task Disable_thinking_off_sends_neither_kwarg_nor_cap()
    {
        var handler = new FakeHandler(_ => Json(Completion("body")));
        await Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None);

        var body = handler.Bodies.Single();
        Assert.DoesNotContain("chat_template_kwargs", body);
        Assert.DoesNotContain("max_tokens", body);
    }

    [Fact]
    public async Task Explicit_max_tokens_wins_over_the_no_think_default()
    {
        var handler = new FakeHandler(_ => Json(Completion("body")));
        await Provider(handler, disableThinking: true)
            .SendMessageAsync("SYS", "USER", CancellationToken.None, maxTokens: 32);
        Assert.Contains("\"max_tokens\":32", handler.Bodies.Single());
    }

    [Fact]
    public async Task Reasoning_content_is_folded_into_the_raw_response_for_the_trace()
    {
        var body = """
            {
              "choices": [{
                "message": { "role": "assistant", "content": "The final answer.", "reasoning_content": "long deliberation" },
                "finish_reason": "stop"
              }]
            }
            """;
        var handler = new FakeHandler(_ => Json(body));
        var result = await Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None);
        Assert.Equal("The final answer.", result.CleanedText);
        Assert.Contains("<think>", result.RawResponse);
        Assert.Contains("long deliberation", result.RawResponse);
        Assert.Contains("The final answer.", result.RawResponse);
    }

    [Fact]
    public async Task Missing_usage_field_is_tolerated_as_null_tokens()
    {
        var handler = new FakeHandler(_ => Json(Completion("body", withUsage: false)));
        var result = await Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None);
        Assert.Null(result.TokensUsed);
    }

    [Fact]
    public async Task Unauthorized_names_the_key_setting()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<BriefGenerationException>(() =>
            Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None));
        Assert.Contains("OPENWEBUI_API_KEY", ex.Message);
    }

    [Fact]
    public async Task Timeout_reports_a_readable_error()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(Completion("too late"));
        });
        var ex = await Assert.ThrowsAsync<ModelTimeoutException>(() =>
            Provider(handler, TimeSpan.FromMilliseconds(100))
                .SendMessageAsync("SYS", "USER", CancellationToken.None));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task Connection_refused_reads_as_server_unreachable()
    {
        var handler = new FakeHandler((HttpRequestMessage _) =>
            throw new HttpRequestException("No connection could be made"));
        var ex = await Assert.ThrowsAsync<BriefGenerationException>(() =>
            Provider(handler).SendMessageAsync("SYS", "USER", CancellationToken.None));
        Assert.Contains("unreachable", ex.Message);
    }

    [Fact]
    public async Task Missing_configuration_fails_before_any_network_call()
    {
        var handler = new FakeHandler(_ => Json(Completion("x")));
        var provider = new OpenWebUiBriefProvider(new HttpClient(handler), () => (null, null, null));
        var ex = await Assert.ThrowsAsync<BriefGenerationException>(() =>
            provider.SendMessageAsync("SYS", "USER", CancellationToken.None));
        Assert.Contains("OPENWEBUI_BASE_URL", ex.Message);
        Assert.Empty(handler.Requests);
    }
}
