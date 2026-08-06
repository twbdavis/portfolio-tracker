using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Api.Tests;

public class ResearchQueueEndpointTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public ResearchQueueEndpointTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Queue_renders_from_local_data_with_portfolio_context()
    {
        var core = _fx.Host.Core;
        var id = core.Universes.Insert($"QueueTest-{Guid.NewGuid():N}", isSeeded: false);
        core.Universes.AddMembers(id, new[] { "QQT1", "QQT2" });

        var queue = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/queue");
        var tickers = queue.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("ticker").GetString()).ToList();
        Assert.Contains("QQT1", tickers);
        Assert.Contains("QQT2", tickers);

        var row = queue.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("ticker").GetString() == "QQT1");
        // every row must ship its full evidence breakdown — no bare numbers
        Assert.Equal(6, row.GetProperty("evidence").GetArrayLength());
        Assert.True(queue.TryGetProperty("portfolioSectorWeights", out _));

        core.Universes.Delete(id);
    }

    [Fact]
    public async Task Queue_dismiss_snoozes_the_ticker()
    {
        var core = _fx.Host.Core;
        var id = core.Universes.Insert($"SnoozeTest-{Guid.NewGuid():N}", isSeeded: false);
        core.Universes.AddMembers(id, new[] { "SNZE" });

        var dismiss = await _fx.Client.PostAsync("/api/queue/SNZE/dismiss", null);
        dismiss.EnsureSuccessStatusCode();

        var queue = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/queue");
        Assert.DoesNotContain(queue.GetProperty("rows").EnumerateArray(),
            r => r.GetProperty("ticker").GetString() == "SNZE");

        core.Universes.Delete(id);
        core.QueueSnoozes.Unsnooze("SNZE");
    }

    [Fact]
    public async Task Discoveries_accept_and_ignore_flow()
    {
        var core = _fx.Host.Core;
        core.Discovered.InsertIfNew("DSCA", "MU", DateTimeOffset.Now);
        core.Discovered.InsertIfNew("DSCB", "MU", DateTimeOffset.Now);

        var list = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/discoveries");
        var tickers = list.EnumerateArray().Select(d => d.GetProperty("ticker").GetString()).ToList();
        Assert.Contains("DSCA", tickers);
        Assert.Contains("DSCB", tickers);

        var accept = await _fx.Client.PostAsJsonAsync("/api/discoveries/DSCA/accept",
            new { universeId = (long?)null });
        accept.EnsureSuccessStatusCode();
        Assert.Equal(DiscoveryStatus.Accepted, core.Discovered.Get("DSCA")!.Status);
        Assert.Contains("DSCA", core.Universes.GetAllTickers());

        var ignore = await _fx.Client.PostAsync("/api/discoveries/DSCB/ignore", null);
        ignore.EnsureSuccessStatusCode();
        Assert.Equal(DiscoveryStatus.Ignored, core.Discovered.Get("DSCB")!.Status);

        var refreshed = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/discoveries");
        Assert.DoesNotContain(refreshed.EnumerateArray(),
            d => d.GetProperty("ticker").GetString() is "DSCA" or "DSCB");

        var missing = await _fx.Client.PostAsync("/api/discoveries/NOPE/ignore", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Scoreboard_reports_rows_and_setting_state()
    {
        var body = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/signals/scoreboard");
        Assert.True(body.TryGetProperty("rows", out var rows));
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.True(body.TryGetProperty("downWeightingEnabled", out _));
    }

    [Fact]
    public async Task Brief_request_with_no_provider_is_refused_not_crashed()
    {
        var resp = await _fx.Client.PostAsync("/api/research/MU/brief", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("choose a provider", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Ai_usage_meter_reports_disabled_without_a_provider()
    {
        var usage = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/ai/usage");
        Assert.False(usage.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", usage.GetProperty("provider").GetString());
        Assert.Equal(500_000, usage.GetProperty("budget").GetInt32());
    }

    [Fact]
    public async Task Brief_status_reports_idle_when_nothing_is_generating()
    {
        var status = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/briefs/status");
        Assert.False(status.GetProperty("active").GetBoolean());
        Assert.Equal(4, status.GetProperty("totalSections").GetInt32());
    }

    [Fact]
    public async Task Test_ai_without_a_provider_reports_the_problem_readably()
    {
        var resp = await _fx.Client.PostAsync("/api/settings/test-ai", null);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Prompt_templates_reset_seeds_the_editable_files()
    {
        var resp = await _fx.Client.PostAsync("/api/settings/prompts/reset", null);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var dir = body.GetProperty("dir").GetString()!;
        Assert.True(File.Exists(Path.Combine(dir, "system.md")));
        Assert.True(File.Exists(Path.Combine(dir, "section-key-numbers.md")));
        Assert.True(File.Exists(Path.Combine(dir, "repair.md")));
    }

    [Fact]
    public async Task Jobs_run_on_demand_and_are_idempotent()
    {
        var first = await _fx.Client.PostAsync("/api/queue/jobs/run", null);
        first.EnsureSuccessStatusCode();
        var second = await _fx.Client.PostAsync("/api/queue/jobs/run", null);
        second.EnsureSuccessStatusCode();
    }
}
