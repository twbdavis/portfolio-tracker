using System.Text.Json;

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
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
