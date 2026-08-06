using System.Text.Json;

namespace PortfolioTracker.Core.Research;

/// Research-queue tuning, user-editable in queue.json in the app data folder.
/// Re-read on every queue build (hot reload without restart); a missing file
/// is written out with these documented defaults. Weights shape which evidence
/// counts most — they never blend dimensions into a hidden composite, because
/// every row still shows its full pass/fail breakdown.
public class QueueSettings
{
    /// Per-dimension weights. Rank = Σ(weight × pass) / Σ(weight of non-n/a
    /// dimensions). Portfolio fit ships heavier: diversification evidence is
    /// the one dimension the market data can't provide twice.
    public decimal WeightSignals { get; set; } = 1.0m;
    public decimal WeightSectorHealth { get; set; } = 1.0m;
    public decimal WeightRelativeValue { get; set; } = 1.0m;
    public decimal WeightQuality { get; set; } = 1.0m;
    public decimal WeightCatalyst { get; set; } = 1.0m;
    public decimal WeightPortfolioFit { get; set; } = 1.5m;

    /// Portfolio fit passes below this correlation.
    public decimal FitCorrelationMax { get; set; } = 0.4m;
    /// Sector overlap flag: candidate's sector already at least this share of
    /// portfolio value.
    public decimal SectorOverlapPct { get; set; } = 25m;

    /// Relative value / quality pass in the best tercile of their sector
    /// (percentile at or above this).
    public decimal TercilePct { get; set; } = 100m * 2m / 3m;

    /// Catalyst proximity: earnings within this many days AND at least
    /// MinBeats of the last four quarters beat estimates.
    public int CatalystDaysToEarnings { get; set; } = 14;
    public int CatalystMinBeats { get; set; } = 3;

    /// "Dismiss from queue" snoozes a ticker for this many days.
    public int SnoozeDays { get; set; } = 30;

    public static QueueSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<QueueSettings>(File.ReadAllText(path)) ?? Write(path);
        }
        catch (JsonException) { /* corrupted: fall through to defaults, don't clobber the file */ }
        catch (IOException) { }
        return File.Exists(path) ? new QueueSettings() : Write(path);
    }

    private static QueueSettings Write(string path)
    {
        var defaults = new QueueSettings();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(defaults,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { /* read-only app dir: run on defaults */ }
        return defaults;
    }
}
