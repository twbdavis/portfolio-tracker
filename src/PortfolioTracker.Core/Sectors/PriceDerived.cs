using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Sectors;

public enum SmaCrossState { Unknown = 0, None = 1, GoldenCross = 2, DeathCross = 3 }

/// Values derived from one ticker's daily-close series. Everything is null
/// until the series is long enough — a short series emits nulls, never a
/// number computed from insufficient data.
public record DerivedMetrics(
    decimal? Price, DateOnly? PriceDate,
    decimal? PctOff52wHigh,
    decimal? Return5d, decimal? Return20d, decimal? Return30d, decimal? Return60d,
    decimal? Sma50, decimal? Sma200,
    SmaCrossState CrossState,
    int BarCount)
{
    public static readonly DerivedMetrics Empty =
        new(null, null, null, null, null, null, null, null, null, SmaCrossState.Unknown, 0);
}

public static class PriceDerived
{
    /// Sessions a golden/death cross may be old and still count as "recent".
    public const int CrossLookback = 5;

    /// Bars needed before an SMA-200-based value (incl. cross state) is emitted.
    public const int MinBarsForSma200 = 200 + CrossLookback;

    public static DerivedMetrics Compute(IReadOnlyList<DailyCloseRow> closesOldestFirst,
        decimal? wk52High = null)
    {
        var closes = closesOldestFirst;
        if (closes.Count == 0) return DerivedMetrics.Empty;

        var last = closes[^1];
        var price = last.Close;

        // 52w-high distance prefers the crawled metric; falls back to the
        // series max only when the series plausibly spans a year
        var high = wk52High;
        if (high is null && closes.Count >= 200)
            high = closes.Skip(Math.Max(0, closes.Count - 252)).Max(c => c.Close);
        decimal? pctOff = high is decimal h && h > 0m ? (h - price) / h * 100m : null;

        return new DerivedMetrics(
            price, last.Date,
            pctOff,
            ReturnOver(closes, 5), ReturnOver(closes, 20), ReturnOver(closes, 30), ReturnOver(closes, 60),
            Sma(closes, 50), Sma(closes, 200),
            CrossState(closes),
            closes.Count);
    }

    /// Percent return over the last n sessions; needs n+1 bars.
    public static decimal? ReturnOver(IReadOnlyList<DailyCloseRow> closes, int n)
    {
        if (closes.Count < n + 1) return null;
        var then = closes[^(n + 1)].Close;
        return then == 0m ? null : (closes[^1].Close - then) / then * 100m;
    }

    /// Simple moving average of the last n closes; needs n bars.
    public static decimal? Sma(IReadOnlyList<DailyCloseRow> closes, int n, int endOffset = 0)
    {
        if (closes.Count - endOffset < n || endOffset < 0) return null;
        decimal sum = 0m;
        for (var i = closes.Count - endOffset - n; i < closes.Count - endOffset; i++)
            sum += closes[i].Close;
        return sum / n;
    }

    /// Did SMA50 cross SMA200 within the last CrossLookback sessions?
    public static SmaCrossState CrossState(IReadOnlyList<DailyCloseRow> closes)
    {
        if (closes.Count < MinBarsForSma200) return SmaCrossState.Unknown;
        for (var offset = 0; offset < CrossLookback; offset++)
        {
            var fastNow = Sma(closes, 50, offset)!.Value;
            var slowNow = Sma(closes, 200, offset)!.Value;
            var fastPrev = Sma(closes, 50, offset + 1)!.Value;
            var slowPrev = Sma(closes, 200, offset + 1)!.Value;
            if (fastPrev <= slowPrev && fastNow > slowNow) return SmaCrossState.GoldenCross;
            if (fastPrev >= slowPrev && fastNow < slowNow) return SmaCrossState.DeathCross;
        }
        return SmaCrossState.None;
    }

    /// Above-SMA50 flags for the last `days` sessions, newest last; null when
    /// the series can't support SMA50 that far back. Used for sector breadth
    /// history (BreadthThrust).
    public static IReadOnlyList<bool>? AboveSma50Series(IReadOnlyList<DailyCloseRow> closes, int days)
    {
        if (closes.Count < 50 + days - 1) return null;
        var flags = new bool[days];
        for (var i = 0; i < days; i++)
        {
            var offset = days - 1 - i;
            flags[i] = closes[^(offset + 1)].Close > Sma(closes, 50, offset)!.Value;
        }
        return flags;
    }
}
