using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Sectors;

public static class SignalKinds
{
    public const string QualityOnSale = "QualityOnSale";
    public const string CheapGrowth = "CheapGrowth";
    public const string RecMomentum = "RecMomentum";
    public const string GoldenCross = "GoldenCross";
    public const string DeathCross = "DeathCross";
    public const string SectorDivergence = "SectorDivergence";
    public const string CatalystWatch = "CatalystWatch";
    public const string BreadthThrust = "BreadthThrust";
}

public record SignalConditionResult(string Label, string Observed, string Threshold, bool Pass);

/// The full arithmetic behind one firing — serialized into SignalHit.InputsJson
/// so the "why this fired" panel can replay it verbatim. Strength (0-100)
/// quantifies how far past its thresholds the rule fired — a margin measure
/// for ranking hits against each other, NOT a predicted return and NOT a
/// cross-ticker composite score. Nullable so hits stored before the field
/// existed keep deserializing.
public record SignalWhy(string Summary, string? Sector, List<SignalConditionResult> Conditions,
    int? Strength = null);

/// For BreadthThrust the "ticker" is the sector name — it's a sector-level signal.
public record SignalCandidate(string Ticker, string Kind, SignalWhy Why);

public record EarningsInfo(List<EarningsQuarter> Quarters, DateOnly? NextEarningsDate);

public record SignalInputs(
    Dictionary<string, List<TickerAnalysis>> Groups,
    IReadOnlyDictionary<string, List<RecommendationSnapshotRow>> RecommendationsByTicker,
    IReadOnlyDictionary<string, List<DailyCloseRow>> ClosesByTicker,
    IReadOnlyDictionary<string, EarningsInfo> EarningsByTicker);

/// Rule-based opportunity screening over the warehouse. Deliberately: no
/// composite score, no ML, no buy/sell language — each rule flags "worth a
/// look" and records every observed value and threshold. Pure function of its
/// inputs and the settings, so evaluations are deterministic and reproducible
/// from warehouse state.
public static class SignalEngine
{
    public static List<SignalCandidate> Evaluate(SignalInputs inputs, SignalSettings s, DateOnly today)
    {
        var hits = new List<SignalCandidate>();
        foreach (var (sector, rows) in inputs.Groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (sector == SectorAnalyzer.Unclassified) continue;   // no sector context, no sector signals
            var rankings = SectorAnalyzer.Rankings(rows).ToDictionary(r => r.Ticker, StringComparer.OrdinalIgnoreCase);

            foreach (var t in rows)
            {
                var rank = rankings[t.Ticker];
                AddIfFired(hits, QualityOnSale(t, rank, rows, rankings, s, sector));
                AddIfFired(hits, CheapGrowth(t, rank, s, sector));
                AddIfFired(hits, RecMomentum(t, inputs.RecommendationsByTicker, s, sector));
                AddIfFired(hits, SmaCross(t, sector));
                AddIfFired(hits, SectorDivergence(t, rows, s, sector));
                AddIfFired(hits, CatalystWatch(t, inputs.EarningsByTicker, s, sector, today));
            }

            AddIfFired(hits, BreadthThrust(sector, rows, inputs.ClosesByTicker, s));
        }
        return hits;
    }

    private static void AddIfFired(List<SignalCandidate> hits, SignalCandidate? candidate)
    {
        if (candidate is not null) hits.Add(candidate);
    }

    // ---- strength helpers -----------------------------------------------------

    /// Linear 0..1 position of value between the firing threshold (`atThreshold`
    /// -> 0) and a "decisive" level (`atStrong` -> 1), clamped.
    private static decimal Unit(decimal value, decimal atThreshold, decimal atStrong)
    {
        if (atStrong == atThreshold) return 0m;
        var u = (value - atThreshold) / (atStrong - atThreshold);
        return Math.Clamp(u, 0m, 1m);
    }

    /// Mean of the per-condition margins, as 0-100.
    private static int Score(params decimal[] units) =>
        units.Length == 0 ? 0 : (int)Math.Round(units.Average() * 100m);

    // ---- 1. QualityOnSale ---------------------------------------------------

    private static SignalCandidate? QualityOnSale(TickerAnalysis t, SectorTickerRow rank,
        List<TickerAnalysis> rows, Dictionary<string, SectorTickerRow> rankings,
        SignalSettings s, string sector)
    {
        if (t.Derived.PctOff52wHigh is not decimal pctOff) return null;
        var pctOffPop = rows.Where(r => r.Derived.PctOff52wHigh is not null)
            .Select(r => r.Derived.PctOff52wHigh!.Value).ToList();
        if (RobustStats.Percentile(pctOff, pctOffPop) is not decimal offPercentile) return null;

        if (Composite(rank) is not decimal composite) return null;
        var compositePop = rankings.Values.Select(Composite).Where(c => c is not null)
            .Select(c => c!.Value).ToList();
        if (RobustStats.Percentile(composite, compositePop) is not decimal compPercentile) return null;

        var offPass = offPercentile >= s.QualityOnSaleWorstDecilePct;
        var compPass = compPercentile >= s.QualityOnSaleTopQuartilePct;
        if (!offPass || !compPass) return null;

        return new SignalCandidate(t.Ticker, SignalKinds.QualityOnSale, new SignalWhy(
            $"{t.Ticker} is {pctOff:F1}% off its 52-week high (deeper than {offPercentile:F0}% of {sector}) " +
            $"while its fundamentals composite ranks in the sector's top quartile.",
            sector,
            new List<SignalConditionResult>
            {
                new("% off 52w high percentile within sector", $"{offPercentile:F0}%",
                    $"≥ {s.QualityOnSaleWorstDecilePct:F0}% (worst decile)", offPass),
                new("Fundamentals composite (mean of growth/margin/ROE z) percentile",
                    $"{compPercentile:F0}% (z̄ = {composite:F2})",
                    $"≥ {s.QualityOnSaleTopQuartilePct:F0}% (top quartile)", compPass),
            },
            Score(Unit(offPercentile, s.QualityOnSaleWorstDecilePct, 100m),
                  Unit(compPercentile, s.QualityOnSaleTopQuartilePct, 100m))));
    }

    /// Mean of the growth/net-margin/ROE z-scores; needs at least two of the
    /// three so a single metric can't masquerade as "quality".
    private static decimal? Composite(SectorTickerRow r)
    {
        var zs = new[] { r.ZGrowth, r.ZNetMargin, r.ZRoe }.Where(z => z is not null)
            .Select(z => z!.Value).ToList();
        return zs.Count >= 2 ? zs.Average() : null;
    }

    // ---- 2. CheapGrowth -----------------------------------------------------

    private static SignalCandidate? CheapGrowth(TickerAnalysis t, SectorTickerRow rank,
        SignalSettings s, string sector)
    {
        if (rank.ZPe is not decimal zPe || rank.ZGrowth is not decimal zGrowth) return null;
        var cheapPass = zPe >= s.CheapGrowthPeZMin;
        var growthPass = zGrowth >= s.CheapGrowthGrowthZMin;
        if (!cheapPass || !growthPass) return null;

        return new SignalCandidate(t.Ticker, SignalKinds.CheapGrowth, new SignalWhy(
            $"{t.Ticker} trades at a P/E of {rank.Pe:F1} — markedly cheap for {sector} " +
            $"(cheapness z {zPe:F2}) — with sector-median-or-better revenue growth (z {zGrowth:F2}).",
            sector,
            new List<SignalConditionResult>
            {
                new("P/E cheapness z (positive = cheap; robust, vs sector)",
                    $"{zPe:F2} (P/E {rank.Pe:F1})", $"≥ {s.CheapGrowthPeZMin:F1}", cheapPass),
                new("Revenue growth z (vs sector)",
                    $"{zGrowth:F2} ({rank.RevenueGrowthYoY:F1}% YoY)",
                    $"≥ {s.CheapGrowthGrowthZMin:F1}", growthPass),
            },
            Score(Unit(zPe, s.CheapGrowthPeZMin, s.CheapGrowthPeZMin + 2m),
                  Unit(zGrowth, s.CheapGrowthGrowthZMin, s.CheapGrowthGrowthZMin + 2m))));
    }

    // ---- 3. RecMomentum -----------------------------------------------------

    private static SignalCandidate? RecMomentum(TickerAnalysis t,
        IReadOnlyDictionary<string, List<RecommendationSnapshotRow>> recsByTicker,
        SignalSettings s, string sector)
    {
        if (!recsByTicker.TryGetValue(t.Ticker, out var recs)) return null;
        var needed = s.RecMomentumRisingMonths + 1;   // N rises need N+1 snapshots
        if (recs.Count < needed) return null;
        var window = recs.Take(needed).ToList();      // newest first
        if (window.Any(r => r.BuyRatio is null)) return null;

        var rising = true;
        for (var i = 0; i < s.RecMomentumRisingMonths; i++)
            rising &= window[i].BuyRatio > window[i + 1].BuyRatio;
        var analysts = window[0].Total;
        var analystsPass = analysts >= s.RecMomentumMinAnalysts;
        if (!rising || !analystsPass) return null;

        var trail = string.Join(" → ", window.AsEnumerable().Reverse()
            .Select(r => $"{r.BuyRatio!.Value:P0}"));
        // strength: total buy-ratio gain over the window (20pp = decisive) and
        // coverage depth (25 analysts = decisive)
        var totalRise = window[0].BuyRatio!.Value - window[^1].BuyRatio!.Value;
        return new SignalCandidate(t.Ticker, SignalKinds.RecMomentum, new SignalWhy(
            $"{t.Ticker}'s analyst buy ratio rose {s.RecMomentumRisingMonths} months running ({trail}) " +
            $"across {analysts} analysts.",
            sector,
            new List<SignalConditionResult>
            {
                new($"Buy ratio rose each of the last {s.RecMomentumRisingMonths} monthly snapshots",
                    trail, "strictly rising", rising),
                new("Analysts covering (latest month)", analysts.ToString(),
                    $"≥ {s.RecMomentumMinAnalysts}", analystsPass),
            },
            Score(Unit(totalRise, 0m, 0.20m),
                  Unit(analysts, s.RecMomentumMinAnalysts, 25m))));
    }

    // ---- 4. GoldenCross / DeathCross ---------------------------------------

    private static SignalCandidate? SmaCross(TickerAnalysis t, string sector)
    {
        var state = t.Derived.CrossState;
        if (state is not SmaCrossState.GoldenCross and not SmaCrossState.DeathCross) return null;
        var kind = state == SmaCrossState.GoldenCross ? SignalKinds.GoldenCross : SignalKinds.DeathCross;
        var direction = state == SmaCrossState.GoldenCross ? "above" : "below";
        // strength: how decisively the averages have separated since crossing
        // (2% of SMA200 = decisive; a graze that may re-cross scores near 0)
        var separationPct = t.Derived.Sma200 is > 0m
            ? Math.Abs((t.Derived.Sma50!.Value - t.Derived.Sma200.Value) / t.Derived.Sma200.Value) * 100m
            : 0m;
        return new SignalCandidate(t.Ticker, kind, new SignalWhy(
            $"{t.Ticker}'s 50-day SMA ({t.Derived.Sma50:F2}) crossed {direction} its 200-day SMA " +
            $"({t.Derived.Sma200:F2}) within the last {PriceDerived.CrossLookback} sessions.",
            sector,
            new List<SignalConditionResult>
            {
                new($"SMA50 crossed {direction} SMA200 in the last {PriceDerived.CrossLookback} sessions",
                    $"SMA50 {t.Derived.Sma50:F2} vs SMA200 {t.Derived.Sma200:F2} ({t.Derived.BarCount} bars)",
                    "cross within lookback", true),
            },
            Score(Unit(separationPct, 0m, 2m))));
    }

    // ---- 5. SectorDivergence ------------------------------------------------

    private static SignalCandidate? SectorDivergence(TickerAnalysis t, List<TickerAnalysis> rows,
        SignalSettings s, string sector)
    {
        if (t.Derived.Return30d is not decimal r30) return null;
        var pop = rows.Where(r => r.Derived.Return30d is not null)
            .Select(r => r.Derived.Return30d!.Value).ToList();
        if (pop.Count < RobustStats.MinPopulation) return null;
        if (RobustStats.Median(pop) is not decimal median) return null;

        var threshold = median - s.SectorDivergenceGapPct;
        if (r30 > threshold) return null;

        return new SignalCandidate(t.Ticker, SignalKinds.SectorDivergence, new SignalWhy(
            $"{t.Ticker} returned {r30:F1}% over 30 days while the {sector} median was {median:F1}% — " +
            $"a {median - r30:F1}pp lag.",
            sector,
            new List<SignalConditionResult>
            {
                new("Ticker 30d return", $"{r30:F1}%",
                    $"≤ sector median ({median:F1}%) − {s.SectorDivergenceGapPct:F0}pp = {threshold:F1}%",
                    true),
            },
            // lag beyond the required gap; double the gap = decisive
            Score(Unit(median - r30, s.SectorDivergenceGapPct, s.SectorDivergenceGapPct * 2m))));
    }

    // ---- 6. CatalystWatch ---------------------------------------------------

    private static SignalCandidate? CatalystWatch(TickerAnalysis t,
        IReadOnlyDictionary<string, EarningsInfo> earningsByTicker,
        SignalSettings s, string sector, DateOnly today)
    {
        if (!earningsByTicker.TryGetValue(t.Ticker, out var e)) return null;
        if (e.NextEarningsDate is not DateOnly next) return null;
        var daysOut = next.DayNumber - today.DayNumber;
        var soonPass = daysOut >= 0 && daysOut <= s.CatalystWatchDaysToEarnings;
        if (!soonPass) return null;

        var quarters = e.Quarters.Take(s.CatalystWatchBeatQuarters).ToList();
        if (quarters.Count < s.CatalystWatchBeatQuarters) return null;
        var allBeats = quarters.All(q => q.Actual is not null && q.Estimate is not null
                                         && q.Actual > q.Estimate);
        if (!allBeats) return null;

        var beatsText = string.Join(", ", quarters.AsEnumerable().Reverse()
            .Select(q => $"{q.Period}: {q.Actual:F2} vs {q.Estimate:F2}"));
        // strength: average beat margin (10% avg surprise = decisive) plus
        // imminence of the next report
        var margins = quarters
            .Where(q => Math.Abs(q.Estimate!.Value) >= 0.01m)
            .Select(q => (q.Actual!.Value - q.Estimate!.Value) / Math.Abs(q.Estimate!.Value) * 100m)
            .ToList();
        var avgBeatPct = margins.Count > 0 ? margins.Average() : 0m;
        return new SignalCandidate(t.Ticker, SignalKinds.CatalystWatch, new SignalWhy(
            $"{t.Ticker} beat EPS estimates in each of the last {s.CatalystWatchBeatQuarters} quarters " +
            $"and reports again on {next:yyyy-MM-dd} ({daysOut} days).",
            sector,
            new List<SignalConditionResult>
            {
                new($"Last {s.CatalystWatchBeatQuarters} quarters all EPS beats", beatsText,
                    "actual > estimate, every quarter", allBeats),
                new("Days to next earnings", daysOut.ToString(),
                    $"≤ {s.CatalystWatchDaysToEarnings}", soonPass),
            },
            Score(Unit(avgBeatPct, 0m, 10m),
                  Unit(s.CatalystWatchDaysToEarnings - daysOut, 0m, s.CatalystWatchDaysToEarnings))));
    }

    // ---- 7. BreadthThrust (sector-level) -------------------------------------

    private static SignalCandidate? BreadthThrust(string sector, List<TickerAnalysis> rows,
        IReadOnlyDictionary<string, List<DailyCloseRow>> closesByTicker, SignalSettings s)
    {
        var days = s.BreadthThrustWindowSessions + 1;
        var perTicker = rows
            .Select(t => closesByTicker.TryGetValue(t.Ticker, out var closes)
                ? PriceDerived.AboveSma50Series(closes, days) : null)
            .Where(flags => flags is not null)
            .ToList();
        if (perTicker.Count < s.BreadthThrustMinTickers) return null;

        // daily breadth series, oldest → newest
        var breadth = new decimal[days];
        for (var d = 0; d < days; d++)
            breadth[d] = (decimal)perTicker.Count(f => f![d]) / perTicker.Count * 100m;

        var current = breadth[^1];
        var priorMin = breadth[..^1].Min();
        var currentPass = current > s.BreadthThrustHighPct;
        var lowPass = priorMin < s.BreadthThrustLowPct;
        if (!currentPass || !lowPass) return null;

        return new SignalCandidate(sector, SignalKinds.BreadthThrust, new SignalWhy(
            $"{sector} breadth (share of tickers above their SMA50) thrust from {priorMin:F0}% to " +
            $"{current:F0}% within {s.BreadthThrustWindowSessions} sessions ({perTicker.Count} tickers measured).",
            sector,
            new List<SignalConditionResult>
            {
                new($"Breadth low within the last {s.BreadthThrustWindowSessions} sessions",
                    $"{priorMin:F0}%", $"< {s.BreadthThrustLowPct:F0}%", lowPass),
                new("Breadth now", $"{current:F0}%", $"> {s.BreadthThrustHighPct:F0}%", currentPass),
            },
            // swing beyond the minimum required (low->high); a full 0->100 sweep = decisive
            Score(Unit(current - priorMin,
                s.BreadthThrustHighPct - s.BreadthThrustLowPct, 90m))));
    }
}
