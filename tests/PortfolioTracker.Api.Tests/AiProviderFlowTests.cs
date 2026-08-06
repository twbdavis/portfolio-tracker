using System.Net.Http.Json;
using System.Text.Json;

namespace PortfolioTracker.Api.Tests;

/// The exact flow a user follows in Settings: pick a provider, fill its
/// fields, save — the usage endpoint must immediately report enabled.
public class AiProviderFlowTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public AiProviderFlowTests(ApiFixture fx) => _fx = fx;

    private async Task PutSettings(object overrides)
    {
        var defaults = new Dictionary<string, object?>
        {
            ["finnhubApiKey"] = null,
            ["refreshIntervalSeconds"] = 60,
            ["showClosedPositions"] = false,
            ["theme"] = "dark",
            ["filingStatus"] = "Single",
            ["otherIncome"] = 0m,
            ["startWithWindows"] = false,
        };
        foreach (var prop in overrides.GetType().GetProperties())
            defaults[char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]] = prop.GetValue(overrides);
        var resp = await _fx.Client.PutAsJsonAsync("/api/settings", defaults);
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Selecting_openwebui_with_fields_enables_briefs_immediately()
    {
        await PutSettings(new
        {
            AiProvider = "openwebui",
            OpenWebUiBaseUrl = "https://ai.unit.test",
            OpenWebUiApiKey = "unit-key",
            OpenWebUiModel = "unit-model",
        });

        var usage = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/ai/usage");
        Assert.Equal("openwebui", usage.GetProperty("provider").GetString());
        Assert.True(usage.GetProperty("enabled").GetBoolean());
        Assert.False(usage.GetProperty("budgetEnforced").GetBoolean());
    }

    [Fact]
    public async Task Test_connection_uses_the_forms_values_before_they_are_saved()
    {
        // nothing saved: provider comes from the request body, and the missing
        // URL is reported by name — not a generic "disabled" message
        var resp = await _fx.Client.PostAsJsonAsync("/api/settings/test-ai", new
        {
            aiProvider = "openwebui",
            openWebUiBaseUrl = (string?)null,
            openWebUiApiKey = "typed-but-unsaved",
            openWebUiModel = "typed-model",
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("OPENWEBUI_BASE_URL", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Selecting_a_provider_without_its_fields_is_configured_but_not_enabled()
    {
        await PutSettings(new
        {
            AiProvider = "openwebui",
            OpenWebUiBaseUrl = (string?)null,
            OpenWebUiApiKey = (string?)null,
            OpenWebUiModel = (string?)null,
        });

        var usage = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/ai/usage");
        Assert.Equal("openwebui", usage.GetProperty("provider").GetString());
        Assert.False(usage.GetProperty("enabled").GetBoolean());
    }
}
