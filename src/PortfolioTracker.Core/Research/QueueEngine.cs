using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Research;

public enum EvidenceStatus
{
    Pass,
    Fail,
    /// Counts toward the denominator but adds nothing to the numerator —
    /// distinct from NotAvailable, which is excluded from both.
    Neutral,
    NotAvailable,
}

public static class QueueDimensions
{
    public const string Signals = "Signals";
    public const string SectorHealth = "SectorHealth";
    public const string RelativeValue = "RelativeValue";
    public const string Quality = "Quality";
    public const string Catalyst = "Catalyst";
    public const string PortfolioFit = "PortfolioFit";
}

/// One evidence dimension for one ticker: what was observed, what the
/// threshold was, and what it contributed. AppliedWeight can differ from
/// Weight only for the signals dimension under scoreboard down-weighting —
/// and Note then says exactly why.
public record EvidenceItem(
    string Dimension, string Label, EvidenceStatus Status,
    string Observed, string Threshold, string? AsOf,
    decimal Weight, decimal AppliedWeight, string? Note = null);

public record QueueCandidate(
    string Ticker, string? Sector,
    decimal Score, decimal WeightedPasses, decimal ApplicableWeight,
    decimal? FundamentalsRank, List<EvidenceItem> Evidence);

public record QueueInputs(
    IReadOnlyCollection<string> CandidateTickers,
    Dictionary<string, List<TickerAnalysis>> Groups,
    IReadOnlyDictionary<string, List<string>> ActiveSignalKindsByTicker,
    IReadOnlyDictionary<string, SectorQuadrant?> QuadrantBySector,
    IReadOnlyDictionary<string, EarningsInfo> EarningsByTicker,
    IReadOnlyDictionary<string, PortfolioFitRow> FitByTicker,
    /// Signal kind → reason, when scoreboard down-weighting is on; empty otherwise.
    IReadOnlyDictionary<string, string> DownWeightedKinds);

/// Evidence-confluence ranking. Deliberately NOT a composite score: the rank
/// is a weighted count of independently-judged pass/fail dimensions, each of
/// which carries its observed values and threshold so any row can be replayed
/// by hand. Dimensions without data are n/a and drop out of that ticker's
/// denominator — missing data must never read as a failing grade.
public static class QueueEngine
{
    public static List<QueueCandidate> Rank(QueueInputs inputs, QueueSettings s, DateOnly today)
    {
        // per-sector context computed once, reused by every member
        var analysisByTicker = new Dictionary<string, TickerAnalysis>(StringComparer.OrdinalIgnoreCase);
        var sectorByTicker = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sector, rows) in inputs.Groups)
            foreach (var t in rows)
            {
                analysisByTicker[t.Ticker] = t;
                sectorByTicker[t.Ticker] = sector;
            }

        var valuationBySector = inputs.Groups.ToDictionary(
            g => g.Key, g => ValuationComposites(g.Value), StringComparer.OrdinalIgnoreCase);
        var qualityBySector = inputs.Groups.ToDictionary(
            g => g.Key, g => QualityComposites(g.Value), StringComparer.OrdinalIgnoreCase);

        var candidates = new List<QueueCandidate>();
        foreach (var ticker in inputs.CandidateTickers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sector = sectorByTicker.GetValueOrDefault(ticker);
            var inSector = sector is not null && sector != SectorAnalyzer.Unclassified;

            var evidence = new List<EvidenceItem>
            {
                SignalsEvidence(ticker, inputs, s),
                SectorHealthEvidence(sector, inSector, inputs, s),
                CompositeTercileEvidence(QueueDimensions.RelativeValue,
                    "Relative value: valuation cheapness vs sector",
                    ticker, inSector ? valuationBySector[sector!] : null,
                    analysisByTicker.GetValueOrDefault(ticker)?.Metrics?.AsOf, s),
                CompositeTercileEvidence(QueueDimensions.Quality,
                    "Quality: fundamentals rank vs sector",
                    ticker, inSector ? qualityBySector[sector!] : null,
                    analysisByTicker.GetValueOrDefault(ticker)?.Metrics?.AsOf, s),
                CatalystEvidence(ticker, inputs, s, today),
                FitEvidence(ticker, inputs, s),
            };

            decimal weightedPasses = 0m, applicableWeight = 0m;
            foreach (var e in evidence)
            {
                if (e.Status == EvidenceStatus.NotAvailable) continue;
                applicableWeight += e.Weight;
                if (e.Status == EvidenceStatus.Pass) weightedPasses += e.AppliedWeight;
            }

            var fundamentalsRank = inSector
                ? qualityBySector[sector!].GetValueOrDefault(ticker)?.Composite
                : null;

            candidates.Add(new QueueCandidate(
                ticker, sector,
                applicableWeight == 0m ? 0m : weightedPasses / applicableWeight,
                weightedPasses, applicableWeight, fundamentalsRank, evidence));
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.FundamentalsRank ?? decimal.MinValue)
            .ThenBy(c => c.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- 1. Signals -----------------------------------------------------------

    private static EvidenceItem SignalsEvidence(string ticker, QueueInputs inputs, QueueSettings s)
    {
        var kinds = inputs.ActiveSignalKindsByTicker.TryGetValue(ticker, out var k)
            ? k.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList()
            : new List<string>();

        if (kinds.Count == 0)
            return new EvidenceItem(QueueDimensions.Signals, "Active undismissed signals",
                EvidenceStatus.Fail, "none", "≥ 1 signal kind active", null,
                s.WeightSignals, s.WeightSignals);

        // scoreboard down-weighting (opt-in): a flagged kind earns half credit;
        // the note spells out which kinds were flagged and why
        var flagged = kinds.Where(inputs.DownWeightedKinds.ContainsKey).ToList();
        var credit = kinds.Sum(kind => inputs.DownWeightedKinds.ContainsKey(kind) ? 0.5m : 1m);
        var multiplier = Math.Min(1m, credit);
        var note = flagged.Count == 0 ? null
            : "Down-weighted: " + string.Join("; ",
                flagged.Select(f => $"{f} ({inputs.DownWeightedKinds[f]})"));

        return new EvidenceItem(QueueDimensions.Signals, "Active undismissed signals",
            EvidenceStatus.Pass,
            $"{kinds.Count} kind{(kinds.Count == 1 ? "" : "s")}: {string.Join(", ", kinds)}",
            "≥ 1 signal kind active", null,
            s.WeightSignals, s.WeightSignals * multiplier, note);
    }

    // ---- 2. Sector health -------------------------------------------------------

    private static EvidenceItem SectorHealthEvidence(string? sector, bool inSector,
        QueueInputs inputs, QueueSettings s)
    {
        var quadrant = inSector ? inputs.QuadrantBySector.GetValueOrDefault(sector!) : null;
        if (quadrant is not SectorQuadrant q)
            return Na(QueueDimensions.SectorHealth, "Sector health quadrant",
                inSector ? "insufficient sector breadth/return data" : "sector unclassified",
                s.WeightSectorHealth);

        var status = q switch
        {
            SectorQuadrant.HealthyRally or SectorQuadrant.Recovering => EvidenceStatus.Pass,
            SectorQuadrant.Topping => EvidenceStatus.Neutral,
            _ => EvidenceStatus.Fail,
        };
        return new EvidenceItem(QueueDimensions.SectorHealth, "Sector health quadrant", status,
            $"{sector}: {q}", "HealthyRally/Recovering pass; Topping neutral; Breakdown fail",
            null, s.WeightSectorHealth, s.WeightSectorHealth);
    }

    // ---- 3 & 4. Relative value / quality (tercile of a sector composite) ---------

    private record CompositeRank(decimal Composite, decimal? Percentile, string Detail);

    /// Valuation cheapness composite: mean of the cheap-positive robust z for
    /// P/E, P/S and P/B (whichever are present). Cheapest tercile = highest
    /// cheapness percentile.
    private static Dictionary<string, CompositeRank?> ValuationComposites(List<TickerAnalysis> rows)
    {
        var pes = Population(rows, t => t.Metrics?.Pe is > 0m ? t.Metrics.Pe : null);
        var pss = Population(rows, t => t.Metrics?.Ps is > 0m ? t.Metrics.Ps : null);
        var pbs = Population(rows, t => t.Metrics?.Pb is > 0m ? t.Metrics.Pb : null);

        decimal? Composite(TickerAnalysis t)
        {
            var zs = new List<decimal>();
            if (t.Metrics?.Pe is > 0m && RobustStats.RobustZ(t.Metrics.Pe.Value, pes) is decimal zPe) zs.Add(-zPe);
            if (t.Metrics?.Ps is > 0m && RobustStats.RobustZ(t.Metrics.Ps.Value, pss) is decimal zPs) zs.Add(-zPs);
            if (t.Metrics?.Pb is > 0m && RobustStats.RobustZ(t.Metrics.Pb.Value, pbs) is decimal zPb) zs.Add(-zPb);
            return zs.Count >= 1 ? zs.Average() : null;
        }

        return Ranks(rows, Composite, c => $"cheapness z̄ = {c:F2}");
    }

    /// Quality composite: mean of growth/net-margin/ROE robust z, at least two
    /// present — same rule as the signals engine, so "quality" means one thing.
    private static Dictionary<string, CompositeRank?> QualityComposites(List<TickerAnalysis> rows)
    {
        var growths = Population(rows, t => t.Metrics?.RevenueGrowthYoY);
        var margins = Population(rows, t => t.Metrics?.NetMargin);
        var roes = Population(rows, t => t.Metrics?.Roe);

        decimal? Composite(TickerAnalysis t)
        {
            var zs = new List<decimal>();
            if (t.Metrics?.RevenueGrowthYoY is decimal g && RobustStats.RobustZ(g, growths) is decimal zg) zs.Add(zg);
            if (t.Metrics?.NetMargin is decimal m && RobustStats.RobustZ(m, margins) is decimal zm) zs.Add(zm);
            if (t.Metrics?.Roe is decimal r && RobustStats.RobustZ(r, roes) is decimal zr) zs.Add(zr);
            return zs.Count >= 2 ? zs.Average() : null;
        }

        return Ranks(rows, Composite, c => $"growth/margin/ROE z̄ = {c:F2}");
    }

    private static Dictionary<string, CompositeRank?> Ranks(List<TickerAnalysis> rows,
        Func<TickerAnalysis, decimal?> composite, Func<decimal, string> describe)
    {
        var values = rows.Select(t => (t.Ticker, Composite: composite(t))).ToList();
        var population = values.Where(v => v.Composite is not null)
            .Select(v => v.Composite!.Value).ToList();
        return values.ToDictionary(
            v => v.Ticker,
            v => v.Composite is not decimal c ? null : new CompositeRank(
                c, RobustStats.Percentile(c, population), describe(c)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static EvidenceItem CompositeTercileEvidence(string dimension, string label,
        string ticker, Dictionary<string, CompositeRank?>? sectorRanks,
        DateTimeOffset? asOf, QueueSettings s)
    {
        var weight = dimension == QueueDimensions.RelativeValue ? s.WeightRelativeValue : s.WeightQuality;
        var rank = sectorRanks?.GetValueOrDefault(ticker);
        if (sectorRanks is null)
            return Na(dimension, label, "sector unclassified", weight);
        if (rank is null)
            return Na(dimension, label, "insufficient fundamentals (common for ETFs)", weight);
        if (rank.Percentile is not decimal pct)
            return Na(dimension, label, "sector population too small to rank", weight);

        var pass = pct >= s.TercilePct;
        return new EvidenceItem(dimension, label,
            pass ? EvidenceStatus.Pass : EvidenceStatus.Fail,
            $"{rank.Detail}, {pct:F0}th percentile of sector",
            $"≥ {s.TercilePct:F0}th percentile (best tercile)",
            asOf?.ToString("yyyy-MM-dd"), weight, weight);
    }

    // ---- 5. Catalyst proximity ---------------------------------------------------

    private static EvidenceItem CatalystEvidence(string ticker, QueueInputs inputs,
        QueueSettings s, DateOnly today)
    {
        if (!inputs.EarningsByTicker.TryGetValue(ticker, out var e))
            return Na(QueueDimensions.Catalyst, "Catalyst proximity",
                "no cached earnings data", s.WeightCatalyst);

        var quarters = e.Quarters.Take(4).ToList();
        var beats = quarters.Count(q => q.Actual is not null && q.Estimate is not null
                                        && q.Actual > q.Estimate);
        string nextText;
        bool soon;
        if (e.NextEarningsDate is DateOnly next)
        {
            var daysOut = next.DayNumber - today.DayNumber;
            soon = daysOut >= 0 && daysOut <= s.CatalystDaysToEarnings;
            nextText = $"next earnings {next:yyyy-MM-dd} ({daysOut}d)";
        }
        else
        {
            soon = false;
            nextText = "next earnings date unknown";
        }

        var pass = soon && beats >= s.CatalystMinBeats && quarters.Count >= 4;
        return new EvidenceItem(QueueDimensions.Catalyst, "Catalyst proximity",
            pass ? EvidenceStatus.Pass : EvidenceStatus.Fail,
            $"{nextText}; {beats} of last {quarters.Count} quarters beat",
            $"earnings within {s.CatalystDaysToEarnings}d AND ≥ {s.CatalystMinBeats} of last 4 beats",
            null, s.WeightCatalyst, s.WeightCatalyst);
    }

    // ---- 6. Portfolio fit ----------------------------------------------------------

    private static EvidenceItem FitEvidence(string ticker, QueueInputs inputs, QueueSettings s)
    {
        var fit = inputs.FitByTicker.GetValueOrDefault(ticker);
        if (fit is null)
            return Na(QueueDimensions.PortfolioFit, "Portfolio fit (correlation)",
                "not yet computed", s.WeightPortfolioFit);
        if (fit.Correlation is not decimal corr)
            return Na(QueueDimensions.PortfolioFit, "Portfolio fit (correlation)",
                $"insufficient overlapping history ({fit.Observations} sessions)", s.WeightPortfolioFit);

        var pass = corr < s.FitCorrelationMax;
        return new EvidenceItem(QueueDimensions.PortfolioFit, "Portfolio fit (correlation)",
            pass ? EvidenceStatus.Pass : EvidenceStatus.Fail,
            $"correlation {corr:0.00} over {fit.Observations} sessions",
            $"< {s.FitCorrelationMax:0.00}", fit.AsOf.ToString("yyyy-MM-dd"),
            s.WeightPortfolioFit, s.WeightPortfolioFit);
    }

    // ---- helpers -------------------------------------------------------------------

    private static EvidenceItem Na(string dimension, string label, string reason, decimal weight) =>
        new(dimension, label, EvidenceStatus.NotAvailable, $"n/a — {reason}",
            "excluded from this ticker's denominator", null, weight, 0m);

    private static List<decimal> Population(IEnumerable<TickerAnalysis> rows,
        Func<TickerAnalysis, decimal?> pick) =>
        rows.Select(pick).Where(v => v is not null).Select(v => v!.Value).ToList();
}
