using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Research;

public record HoldingWeight(string Ticker, decimal Weight);

/// Correlation of a candidate's daily returns against the portfolio composite.
/// Pure functions over warehouse rows — no I/O, no clock — so every path
/// (insufficient data, single holding, candidate that IS a holding) is unit
/// testable.
///
/// Simplification, by design: the composite uses CURRENT portfolio weights
/// applied across the whole trailing window, not historical weights. That
/// answers "how would my portfolio as it stands today have moved with this
/// candidate", which is the fit question — it is not a historical performance
/// reconstruction.
public static class PortfolioFitMath
{
    public const int WindowSessions = 90;
    public const int MinObservations = 60;

    /// Value weights from current holdings. Zero/negative-value holdings are
    /// dropped; an empty result means fit is n/a portfolio-wide.
    public static List<HoldingWeight> Weights(
        IEnumerable<(string Ticker, decimal Shares, decimal Price)> holdings)
    {
        var values = holdings
            .GroupBy(h => h.Ticker.Trim().ToUpperInvariant())
            .Select(g => (Ticker: g.Key, Value: g.Sum(h => h.Shares * h.Price)))
            .Where(h => h.Value > 0m)
            .ToList();
        var total = values.Sum(v => v.Value);
        if (total <= 0m) return new List<HoldingWeight>();
        return values.Select(v => new HoldingWeight(v.Ticker, v.Value / total)).ToList();
    }

    /// Daily simple returns keyed by date, oldest-first input required.
    public static Dictionary<DateOnly, decimal> DailyReturns(IReadOnlyList<DailyCloseRow> closesOldestFirst)
    {
        var returns = new Dictionary<DateOnly, decimal>();
        for (var i = 1; i < closesOldestFirst.Count; i++)
        {
            var prev = closesOldestFirst[i - 1].Close;
            if (prev <= 0m) continue;
            returns[closesOldestFirst[i].Date] = closesOldestFirst[i].Close / prev - 1m;
        }
        return returns;
    }

    /// Value-weighted composite daily return series of the current holdings.
    /// On days where some holdings lack a close, weights renormalize over the
    /// holdings that do have one — a partial day is still a portfolio day.
    public static SortedDictionary<DateOnly, decimal> CompositeReturns(
        IReadOnlyDictionary<string, List<DailyCloseRow>> closesByTicker,
        IReadOnlyList<HoldingWeight> weights)
    {
        var perTicker = new List<(decimal Weight, Dictionary<DateOnly, decimal> Returns)>();
        foreach (var w in weights)
            if (closesByTicker.TryGetValue(w.Ticker, out var closes) && closes.Count >= 2)
                perTicker.Add((w.Weight, DailyReturns(closes)));

        var composite = new SortedDictionary<DateOnly, decimal>();
        foreach (var date in perTicker.SelectMany(t => t.Returns.Keys).Distinct())
        {
            decimal sum = 0m, weightSum = 0m;
            foreach (var (weight, returns) in perTicker)
                if (returns.TryGetValue(date, out var r))
                {
                    sum += weight * r;
                    weightSum += weight;
                }
            if (weightSum > 0m) composite[date] = sum / weightSum;
        }
        return composite;
    }

    /// Pearson correlation of a candidate's returns against the composite over
    /// the composite's trailing `windowSessions` sessions. Correlation is null
    /// (with the overlap count reported) below `minObservations` — a thin
    /// overlap is "not enough data", never a low-confidence number.
    public static (decimal? Correlation, int Observations) Correlate(
        IReadOnlyList<DailyCloseRow> candidateClosesOldestFirst,
        IReadOnlyDictionary<DateOnly, decimal> compositeReturns,
        int windowSessions = WindowSessions,
        int minObservations = MinObservations)
    {
        if (compositeReturns.Count == 0) return (null, 0);
        var window = compositeReturns.Keys.OrderBy(d => d).TakeLast(windowSessions).ToList();
        var candidate = DailyReturns(candidateClosesOldestFirst);

        var pairs = new List<(decimal X, decimal Y)>();
        foreach (var date in window)
            if (candidate.TryGetValue(date, out var r))
                pairs.Add((r, compositeReturns[date]));

        if (pairs.Count < minObservations) return (null, pairs.Count);
        return (Pearson(pairs), pairs.Count);
    }

    /// Null when either series has zero variance — a flat series carries no
    /// correlation information.
    public static decimal? Pearson(IReadOnlyList<(decimal X, decimal Y)> pairs)
    {
        if (pairs.Count < 2) return null;
        var n = pairs.Count;
        var meanX = pairs.Average(p => p.X);
        var meanY = pairs.Average(p => p.Y);
        decimal cov = 0m, varX = 0m, varY = 0m;
        foreach (var (x, y) in pairs)
        {
            var dx = x - meanX;
            var dy = y - meanY;
            cov += dx * dy;
            varX += dx * dx;
            varY += dy * dy;
        }
        if (varX == 0m || varY == 0m) return null;
        var denom = (double)varX * (double)varY;
        var corr = (double)cov / Math.Sqrt(denom);
        return Math.Clamp((decimal)corr, -1m, 1m);
    }

    /// Sector value weights of the current holdings, for the queue header and
    /// the overlap flag. Unclassified tickers roll up under `unclassifiedLabel`.
    public static List<(string Sector, decimal WeightPct)> SectorWeights(
        IReadOnlyList<HoldingWeight> weights,
        IReadOnlyDictionary<string, string> sectorByTicker,
        string unclassifiedLabel = "Unclassified")
    {
        return weights
            .GroupBy(w => sectorByTicker.TryGetValue(w.Ticker, out var s) && !string.IsNullOrWhiteSpace(s)
                ? s : unclassifiedLabel)
            .Select(g => (Sector: g.Key, WeightPct: g.Sum(w => w.Weight) * 100m))
            .OrderByDescending(g => g.WeightPct)
            .ToList();
    }

    /// The plain-language fit line shown next to a candidate, e.g.
    /// "Low correlation (0.21) to your portfolio; you have no Materials exposure."
    public static string FitLine(decimal? correlation, int observations, string? sector,
        decimal sectorWeightPct, decimal overlapThresholdPct)
    {
        string corrPart;
        if (correlation is not decimal c)
            corrPart = observations > 0
                ? $"Not enough overlapping history to correlate ({observations} sessions)"
                : "Not enough price history to correlate";
        else
        {
            var level = Math.Abs(c) switch
            {
                < 0.4m => "Low",
                < 0.7m => "Moderate",
                _ => "High",
            };
            corrPart = $"{level} correlation ({c:0.00}) to your portfolio";
        }

        string sectorPart;
        if (string.IsNullOrEmpty(sector) || sector == "Unclassified")
            sectorPart = "sector unknown";
        else if (sectorWeightPct <= 0m)
            sectorPart = $"you have no {sector} exposure";
        else if (sectorWeightPct >= overlapThresholdPct)
            sectorPart = $"{sector} is already {sectorWeightPct:0}% of your portfolio";
        else
            sectorPart = $"{sector} is {sectorWeightPct:0}% of your portfolio";

        return $"{corrPart}; {sectorPart}.";
    }
}
