using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;

namespace PortfolioTracker.Core.Tests;

public class ReturnHistoryMathTests
{
    private static List<DailyCloseRow> Closes(params (int DayOffset, decimal Close)[] points)
    {
        var start = new DateOnly(2025, 1, 1);
        return points.Select(p => new DailyCloseRow
        {
            Ticker = "T", Date = start.AddDays(p.DayOffset), Close = p.Close,
        }).ToList();
    }

    /// Weekly closes over ~2 years, alternating flat and +12% two-month legs.
    private static List<DailyCloseRow> AlternatingSeries()
    {
        var rows = new List<(int, decimal)>();
        for (var week = 0; week < 104; week++)
        {
            // price steps up 12% every other 60-day block, else flat
            var block = week * 7 / 60;
            var price = 100m * (decimal)Math.Pow(1.12, (block + 1) / 2);
            rows.Add((week * 7, Math.Round(price, 4)));
        }
        return Closes(rows.ToArray());
    }

    [Fact]
    public void Rolling_windows_mature_at_the_first_close_past_sixty_days()
    {
        // closes every 30 days at 100, 100, 110, 121: the first window matures
        // at day 60 (+10%), the second at day 90 (+21%)
        var closes = Closes((0, 100m), (30, 100m), (60, 110m), (90, 121m));
        var returns = new List<decimal>();
        for (var i = 0; i < closes.Count; i++)
        {
            var r = OutcomeMath.ForwardReturn(closes, closes[i].Date, closes[i].Close,
                ReturnHistoryMath.HorizonDays);
            if (r is not null) returns.Add(r.Value);
        }
        Assert.Equal(new[] { 10m, 21m }, returns);
    }

    [Fact]
    public void Stats_report_the_share_of_windows_clearing_ten_percent()
    {
        var stats = ReturnHistoryMath.TwoMonth(AlternatingSeries());
        Assert.NotNull(stats);
        Assert.True(stats!.WindowsAnalyzed >= ReturnHistoryMath.MinWindows);
        Assert.InRange(stats.PctReturnedAtLeast10, 1m, 99m);   // mixed record, not all-or-nothing
        Assert.NotNull(stats.MedianReturnPct);
        Assert.True(stats.BestReturnPct >= stats.MedianReturnPct);
        Assert.True(stats.WorstReturnPct <= stats.MedianReturnPct);
        Assert.Equal("2025-01-01", stats.From);
    }

    [Fact]
    public void Thin_history_returns_null_rather_than_a_noisy_percentage()
    {
        // 10 weekly closes: only ~2 windows can mature — below MinWindows
        var closes = Closes(Enumerable.Range(0, 10).Select(i => (i * 7, 100m + i)).ToArray());
        Assert.Null(ReturnHistoryMath.TwoMonth(closes));
    }

    [Fact]
    public void Zero_and_negative_closes_never_start_a_window()
    {
        var points = Enumerable.Range(0, 60).Select(i => (i * 7, i == 0 ? 0m : 100m)).ToArray();
        var stats = ReturnHistoryMath.TwoMonth(Closes(points));
        Assert.NotNull(stats);   // the zero start is skipped, the rest analyze fine
        Assert.Equal(0m, stats!.PctReturnedAtLeast10);   // flat series never clears 10%
    }
}
