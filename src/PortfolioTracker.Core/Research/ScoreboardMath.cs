using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Research;

public record ScoreboardExample(string Ticker, DateOnly FireDate, decimal Excess);

public record ScoreboardHorizon(int Days, int Matured, decimal? MedianExcess,
    decimal? BeatRatePct, ScoreboardExample? Best, ScoreboardExample? Worst,
    bool Sufficient);

public record ScoreboardRow(string SignalKind, int Fires, int Dismissed, decimal? DismissRatePct,
    List<ScoreboardHorizon> Horizons, bool DownWeighted, string? DownWeightReason);

/// Scoreboard aggregation over matured signal outcomes. Informational by
/// default: percentages hide behind an "insufficient data" flag below
/// MinObservations matured outcomes, because a beat rate over six samples is
/// noise wearing a suit.
public static class ScoreboardMath
{
    public const int MinObservations = 10;
    /// Down-weighting eligibility: at least this many matured 60d outcomes...
    public const int DownWeightMinObservations = 20;
    /// ...with a beat rate below this.
    public const decimal DownWeightBeatRatePct = 45m;
    public const int DownWeightHorizon = 60;

    public static List<ScoreboardRow> Build(
        IReadOnlyList<SignalHit> allHits,
        IReadOnlyList<SignalOutcomeRow> outcomes,
        bool downWeightingEnabled)
    {
        var outcomesByKind = outcomes.ToLookup(o => o.SignalKind, StringComparer.OrdinalIgnoreCase);
        var hitsByKind = allHits.ToLookup(h => h.SignalKind, StringComparer.OrdinalIgnoreCase);
        var kinds = hitsByKind.Select(g => g.Key)
            .Union(outcomesByKind.Select(g => g.Key), StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ScoreboardRow>();
        foreach (var kind in kinds)
        {
            var hits = hitsByKind[kind].ToList();
            var kindOutcomes = outcomesByKind[kind].ToList();
            var horizons = OutcomeMath.Horizons
                .Select(h => Horizon(kindOutcomes, h))
                .ToList();

            var dismissed = hits.Count(h => h.Dismissed);
            var (flagged, reason) = DownWeightVerdict(horizons);
            rows.Add(new ScoreboardRow(
                kind, hits.Count, dismissed,
                hits.Count == 0 ? null : (decimal)dismissed / hits.Count * 100m,
                horizons,
                downWeightingEnabled && flagged,
                flagged ? reason : null));
        }
        return rows;
    }

    /// Kinds whose evidence the queue should down-weight, with the reason —
    /// empty unless the setting is on.
    public static Dictionary<string, string> DownWeightedKinds(
        IReadOnlyList<ScoreboardRow> rows, bool enabled)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!enabled) return map;
        foreach (var row in rows)
            if (row.DownWeighted && row.DownWeightReason is string reason)
                map[row.SignalKind] = reason;
        return map;
    }

    private static ScoreboardHorizon Horizon(List<SignalOutcomeRow> outcomes, int days)
    {
        var matured = outcomes
            .Select(o => (o.Ticker, o.FireDate, Excess: Excess(o, days)))
            .Where(o => o.Excess is not null)
            .Select(o => (o.Ticker, o.FireDate, Excess: o.Excess!.Value))
            .ToList();

        if (matured.Count == 0)
            return new ScoreboardHorizon(days, 0, null, null, null, null, false);

        var best = matured.OrderByDescending(o => o.Excess).First();
        var worst = matured.OrderBy(o => o.Excess).First();
        return new ScoreboardHorizon(
            days, matured.Count,
            Sectors.RobustStats.Median(matured.Select(o => o.Excess).ToList()),
            (decimal)matured.Count(o => o.Excess > 0m) / matured.Count * 100m,
            new ScoreboardExample(best.Ticker, best.FireDate, best.Excess),
            new ScoreboardExample(worst.Ticker, worst.FireDate, worst.Excess),
            matured.Count >= MinObservations);
    }

    private static (bool Flagged, string? Reason) DownWeightVerdict(List<ScoreboardHorizon> horizons)
    {
        var h60 = horizons.FirstOrDefault(h => h.Days == DownWeightHorizon);
        if (h60 is null || h60.Matured < DownWeightMinObservations || h60.BeatRatePct is not decimal rate)
            return (false, null);
        if (rate >= DownWeightBeatRatePct) return (false, null);
        return (true, $"60d sector beat-rate {rate:F0}% over {h60.Matured} matured outcomes, " +
                      $"below the {DownWeightBeatRatePct:F0}% floor");
    }

    private static decimal? Excess(SignalOutcomeRow o, int days) => days switch
    {
        30 => o.Excess30,
        60 => o.Excess60,
        90 => o.Excess90,
        _ => null,
    };
}
