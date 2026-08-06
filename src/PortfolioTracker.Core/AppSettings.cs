using System.Text.Json;
using System.Text.Json.Serialization;
using PortfolioTracker.Core.Research;

namespace PortfolioTracker.Core;

public class AppSettings
{
    public string? FinnhubApiKey { get; set; }
    public int RefreshIntervalSeconds { get; set; } = 60;
    /// Finnhub calls/min the universe crawler may use (of the 60 free-tier
    /// total); the rest stays reserved for interactive research and quotes.
    public int CrawlerCallsPerMinute { get; set; } = 30;
    /// Signal kinds allowed to fire Windows toasts. Ships empty: signals live
    /// in the feed only unless explicitly opted in per kind.
    public List<string> SignalToastKinds { get; set; } = new();
    public bool ShowClosedPositions { get; set; }
    public string Theme { get; set; } = "dark";
    // manual baseline for the Daily Gain/Loss total; only honored on DayStartDate
    public decimal? DayStartValue { get; set; }
    public DateOnly? DayStartDate { get; set; }

    /// Peer discoveries land in a review panel by default; opting in routes
    /// them straight into the "Discovered" universe (and the next crawl pass).
    public bool AutoAcceptDiscoveries { get; set; }

    /// Which backend generates AI research briefs: "openwebui" (local server),
    /// "anthropic", or "disabled". Key names below are contract — they appear
    /// verbatim in the git-ignored appsettings.json; the values (server URL,
    /// bearer key) must never land in a tracked file.
    [JsonPropertyName("AI_VISION_PROVIDER")]
    public string? AiProvider { get; set; }

    [JsonPropertyName("OPENWEBUI_BASE_URL")]
    public string? OpenWebUiBaseUrl { get; set; }

    /// Bearer credential to an internal server: read only from local config,
    /// masked in the UI, excluded from logs and from every git-tracked file.
    [JsonPropertyName("OPENWEBUI_API_KEY")]
    public string? OpenWebUiApiKey { get; set; }

    [JsonPropertyName("OPENWEBUI_MODEL")]
    public string? OpenWebUiModel { get; set; }

    /// Seconds to wait on one local-model call before giving up. Large models
    /// on shared hardware — especially thinking models — can legitimately take
    /// minutes per section, so this defaults generous and is user-editable.
    [JsonPropertyName("OPENWEBUI_TIMEOUT_SECONDS")]
    public int OpenWebUiTimeoutSeconds { get; set; } = 300;

    /// Suppress the model's chain-of-thought (enable_thinking=false template
    /// kwarg). Thinking dominates brief wall time — minutes per ~200-token
    /// section on shared hardware — and adds nothing the grounding checker
    /// can use, so this defaults on. Turn off to let a thinking model
    /// deliberate.
    [JsonPropertyName("OPENWEBUI_DISABLE_THINKING")]
    public bool OpenWebUiDisableThinking { get; set; } = true;

    /// Anthropic key for AI research briefs. Stored like the Finnhub key:
    /// appsettings.json in the app data dir, never in the repo. Briefs stay
    /// entirely OFF until a provider is configured.
    public string? AnthropicApiKey { get; set; }
    public string AnthropicModel { get; set; } = "claude-sonnet-4-6";
    /// Hard monthly ceiling on Anthropic tokens (input + output); brief
    /// requests are refused once the meter reaches it.
    public int AiMonthlyTokenBudget { get; set; } = 500_000;
    /// Optional weekly batch: brief the top-N queue candidates automatically.
    public bool AiWeeklyBatchEnabled { get; set; }
    public int AiWeeklyBatchCount { get; set; } = 5;

    /// Opt-in: down-weight queue evidence from signal kinds whose scoreboard
    /// 60d beat-rate is persistently poor. The queue breakdown shows the
    /// applied weight and the reason whenever this changes a row.
    public bool ScoreboardDownWeighting { get; set; }

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PortfolioTracker");

    private string _dir = AppDataDir;

    public static AppSettings Load(string? dir = null)
    {
        dir ??= AppDataDir;
        var file = Path.Combine(dir, "appsettings.json");
        var settings = new AppSettings();
        try
        {
            if (File.Exists(file))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new AppSettings();
        }
        catch (JsonException) { /* corrupted settings: start fresh */ }
        settings._dir = dir;
        settings.AiProvider = NormalizeProvider(settings.AiProvider, settings.AnthropicApiKey);
        return settings;
    }

    /// Settings written before AI_VISION_PROVIDER existed keep working: a
    /// configured Anthropic key means "anthropic", otherwise the feature is
    /// off until the user picks a provider.
    private static string NormalizeProvider(string? value, string? anthropicKey)
    {
        var v = value?.Trim().ToLowerInvariant();
        if (v is BriefProviderKinds.OpenWebUi or BriefProviderKinds.Anthropic or BriefProviderKinds.Disabled)
            return v;
        return string.IsNullOrWhiteSpace(anthropicKey)
            ? BriefProviderKinds.Disabled : BriefProviderKinds.Anthropic;
    }

    public void Save()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
