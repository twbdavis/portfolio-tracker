using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Research;

public record TwoMonthReturnStats(int WindowsAnalyzed, decimal PctReturnedAtLeast10,
    decimal? MedianReturnPct, decimal? BestReturnPct, decimal? WorstReturnPct,
    string From, string To);

/// The empirical answer to "how often has this stock returned 10%+ in two
/// months": every warehouse close starts a rolling window, matured by the
/// first close at or past 60 calendar days later — the same horizon the
/// signal scoreboard uses. Pure function over closes so the boundary cases
/// are unit-testable.
public static class ReturnHistoryMath
{
    public const int HorizonDays = 60;
    public const decimal TargetReturnPct = 10m;
    /// Below this many matured windows the percentage is noise, not a track
    /// record — the stat is withheld entirely.
    public const int MinWindows = 20;

    public static TwoMonthReturnStats? TwoMonth(IReadOnlyList<DailyCloseRow> closesOldestFirst)
    {
        var returns = new List<decimal>();
        var j = 0;
        for (var i = 0; i < closesOldestFirst.Count; i++)
        {
            var start = closesOldestFirst[i];
            if (start.Close <= 0m) continue;
            var target = start.Date.AddDays(HorizonDays);
            if (j <= i) j = i + 1;
            while (j < closesOldestFirst.Count && closesOldestFirst[j].Date < target) j++;
            if (j >= closesOldestFirst.Count) break;   // window not matured; later ones won't be either
            returns.Add((closesOldestFirst[j].Close / start.Close - 1m) * 100m);
        }

        if (returns.Count < MinWindows) return null;
        return new TwoMonthReturnStats(
            returns.Count,
            Math.Round((decimal)returns.Count(r => r >= TargetReturnPct) / returns.Count * 100m, 1),
            Round1(RobustStats.Median(returns)),
            Math.Round(returns.Max(), 1),
            Math.Round(returns.Min(), 1),
            closesOldestFirst[0].Date.ToString("yyyy-MM-dd"),
            closesOldestFirst[^1].Date.ToString("yyyy-MM-dd"));
    }

    private static decimal? Round1(decimal? value) =>
        value is decimal v ? Math.Round(v, 1) : null;
}
