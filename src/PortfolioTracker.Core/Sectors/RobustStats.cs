namespace PortfolioTracker.Core.Sectors;

/// Median/MAD statistics for sector relative-value math. Median and MAD are
/// used instead of mean/stdev deliberately: sector populations are small and
/// fat-tailed (one meme stock or one distressed name would drag a mean-based
/// z-score for the whole sector), while median/MAD ignore outliers entirely.
/// The 1.4826 factor scales MAD to be stdev-consistent for normal data, so
/// z magnitudes stay comparable to textbook z-scores.
public static class RobustStats
{
    public const decimal MadToSigma = 1.4826m;

    /// Robust z needs at least this many observations to mean anything; a
    /// sector of one or two tickers yields null, never a fake 0.
    public const int MinPopulation = 3;

    public static decimal? Median(IReadOnlyCollection<decimal> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    /// Median absolute deviation from the median.
    public static decimal? Mad(IReadOnlyCollection<decimal> values)
    {
        if (Median(values) is not decimal med) return null;
        return Median(values.Select(v => Math.Abs(v - med)).ToList());
    }

    /// (x - median) / (1.4826 * MAD). Null when the population is too small
    /// or has zero spread — a zero-variance sector gives no information about
    /// relative position, so no z-score rather than a divide-by-zero or 0.
    public static decimal? RobustZ(decimal value, IReadOnlyCollection<decimal> population)
    {
        if (population.Count < MinPopulation) return null;
        if (Median(population) is not decimal med || Mad(population) is not decimal mad) return null;
        if (mad == 0m) return null;
        return (value - med) / (MadToSigma * mad);
    }

    /// Percent of the population at or below the value, 0..100. Needs at
    /// least two observations to be a ranking at all.
    public static decimal? Percentile(decimal value, IReadOnlyCollection<decimal> population)
    {
        if (population.Count < 2) return null;
        var atOrBelow = population.Count(v => v <= value);
        return (decimal)atOrBelow / population.Count * 100m;
    }
}
