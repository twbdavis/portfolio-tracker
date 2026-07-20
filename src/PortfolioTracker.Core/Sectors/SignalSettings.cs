using System.Text.Json;

namespace PortfolioTracker.Core.Sectors;

/// Signal thresholds, user-editable in signals.json in the app data folder.
/// The file is re-read on every evaluation (hot reload without restart); a
/// missing file is written out with these documented defaults so there's
/// always something to edit. Numbers here are screening heuristics, not
/// recommendations — see the README section on editing them.
public class SignalSettings
{
    /// Days a prior (ticker, kind) hit suppresses a re-fire while undismissed.
    public int DedupeDays { get; set; } = 14;

    /// QualityOnSale: PctOff52wHigh in the worst decile of its sector AND the
    /// composite fundamentals z (mean of growth/margin/ROE z-scores, at least
    /// two present) in the top quartile.
    public decimal QualityOnSaleWorstDecilePct { get; set; } = 90m;   // pctOff percentile ≥ this
    public decimal QualityOnSaleTopQuartilePct { get; set; } = 75m;   // composite percentile ≥ this

    /// CheapGrowth: cheap-positive PE z ≥ this (equivalent to raw PE z ≤ -1)
    /// AND revenue-growth z ≥ the floor, both within sector.
    public decimal CheapGrowthPeZMin { get; set; } = 1.0m;
    public decimal CheapGrowthGrowthZMin { get; set; } = 0m;

    /// RecMomentum: buy ratio rose in each of the last N monthly snapshots
    /// and the latest month has at least MinAnalysts covering.
    public int RecMomentumRisingMonths { get; set; } = 2;
    public int RecMomentumMinAnalysts { get; set; } = 5;

    /// SectorDivergence: ticker 30d return at or below the sector median 30d
    /// return minus this many percentage points.
    public decimal SectorDivergenceGapPct { get; set; } = 15m;

    /// CatalystWatch: this many consecutive EPS beats AND next earnings
    /// within DaysToEarnings days.
    public int CatalystWatchBeatQuarters { get; set; } = 4;
    public int CatalystWatchDaysToEarnings { get; set; } = 14;

    /// BreadthThrust: sector above-SMA50 breadth crossed from below Low% to
    /// above High% within WindowSessions sessions.
    public decimal BreadthThrustLowPct { get; set; } = 30m;
    public decimal BreadthThrustHighPct { get; set; } = 50m;
    public int BreadthThrustWindowSessions { get; set; } = 10;
    /// Sectors thinner than this have meaningless breadth; skip them.
    public int BreadthThrustMinTickers { get; set; } = 5;

    public static SignalSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<SignalSettings>(File.ReadAllText(path)) ?? Write(path);
        }
        catch (JsonException) { /* corrupted: fall through to defaults, don't clobber the file */ }
        catch (IOException) { }
        return File.Exists(path) ? new SignalSettings() : Write(path);
    }

    private static SignalSettings Write(string path)
    {
        var defaults = new SignalSettings();
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
