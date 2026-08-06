using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Research;

// The grounding contract, as types. Every record below is an explicit
// whitelist: the model receives exactly these fields and nothing else — no
// pass-through JSON, no "extra context". BriefPayloadTests asserts the
// serialized property set stays inside this whitelist.
//
// Two hygiene rules enforced at build time:
// - every number is rounded to presentation precision, because the model
//   quotes payload values verbatim and "49737.783718593855 million" in a
//   memo is absurd;
// - empty blocks serialize as ABSENT (null + omit), because a small model
//   told about an empty list writes bullets about the absence.

public record BriefProfile(string? Name, string? Sector, string? Industry,
    decimal? MarketCapMillions, string? IpoDate, string? Description);

/// Price context so the memo can anchor valuation and drawdown claims.
/// PctOff52wHigh is precomputed so the model never does the arithmetic.
public record BriefPrice(decimal? Current, decimal? Wk52High, decimal? Wk52Low,
    decimal? PctOff52wHigh);

public record BriefEvidence(string Dimension, string Status, string Observed,
    string Threshold, string? AsOf, string? Note);

/// XSectorMedian = Value / SectorMedian, precomputed and rounded, so the
/// model can cite the comparison as a literal — the grounding checker
/// rejects any number the model derives itself.
public record BriefMetric(string Name, decimal? Value, decimal? SectorMedian,
    decimal? XSectorMedian);

/// The numbers behind the sector-health verdict, so "HealthyRally" isn't a
/// bare label the model can't reason about.
public record BriefSectorHealth(string? Quadrant, decimal? BreadthAboveSma50Pct,
    decimal? BreadthAboveSma200Pct, decimal? MedianReturn20dPct);

/// BuyTotal and Total are precomputed because every model sums them anyway
/// ("22 of 24 analysts") and the checker rejects derived arithmetic.
public record BriefRecTrend(string Period, int StrongBuy, int Buy, int Hold,
    int Sell, int StrongSell, int BuyTotal, int Total);

/// "1 of the last 4 quarters beat" as literals, for the same reason.
public record BriefEarningsRecord(int Beats, int Misses, int Quarters);

public record BriefEarningsQuarter(string Period, decimal? EpsEstimate,
    decimal? EpsActual, decimal? SurprisePercent);

/// This stock's own two-month track record, precomputed from warehouse
/// closes — the primary input to the Buy score section.
public record BriefReturnHistory(int WindowsAnalyzed, decimal PctReturnedAtLeast10,
    decimal? MedianReturnPct, decimal? BestReturnPct, decimal? WorstReturnPct,
    string From, string To);

/// How the fired signal kinds performed historically across all tickers
/// (60-day sector-excess outcomes from the scoreboard).
public record BriefSignalRecord(string Kind, int Matured60d, decimal? BeatRate60dPct,
    decimal? MedianExcess60dPct);

public record BriefCondition(string Label, string Observed, string Threshold, bool Pass);

public record BriefSignal(string Kind, string FiredAt, string Summary,
    List<BriefCondition> Conditions);

public record BriefHeadline(string Date, string Source, string Title, string? Summary);

public record BriefPayload(string Ticker, BriefProfile? Profile, BriefPrice? Price,
    List<BriefEvidence>? Evidence, List<BriefMetric>? Fundamentals,
    BriefSectorHealth? SectorHealth, List<BriefRecTrend>? AnalystRecommendations,
    List<BriefEarningsQuarter>? EarningsHistory, BriefEarningsRecord? EarningsRecord,
    string? NextEarningsDate, List<BriefSignal>? Signals,
    BriefReturnHistory? TwoMonthReturnHistory, List<BriefSignalRecord>? SignalTrackRecord,
    string? PortfolioFit, List<BriefHeadline>? Headlines);

public static class BriefPayloadBuilder
{
    /// Fewer, relevant headlines beat ten noisy ones — the Finnhub company
    /// feed mixes in peer and market stories that a small model happily
    /// misattributes to the subject company.
    public const int MaxHeadlines = 6;
    public const int MaxHeadlineSummaryChars = 300;
    public const int MaxDescriptionChars = 500;
    public const int MaxAnalystRecPeriods = 2;
    public const int MaxEarningsQuarters = 4;

    public static BriefPayload Build(
        string ticker,
        CompanyProfile? profile,
        string? sector,
        IReadOnlyList<EvidenceItem> evidence,
        TickerMetricsRow? metrics,
        SectorSummary? sectorSummary,
        IReadOnlyList<(SignalHit Hit, SignalWhy Why)> signals,
        string? portfolioFitLine,
        IReadOnlyList<NewsItem> news,
        QuoteDetail? quote = null,
        IReadOnlyList<RecommendationTrend>? recommendations = null,
        IReadOnlyList<EarningsQuarter>? earnings = null,
        TwoMonthReturnStats? returnHistory = null,
        IReadOnlyList<ScoreboardRow>? signalRecords = null,
        string? description = null,
        FundamentalMetrics? liveMetrics = null,
        DateOnly? nextEarningsDate = null)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        return new BriefPayload(
            ticker,
            profile is null && sector is null && description is null ? null : new BriefProfile(
                profile?.Name, sector, profile?.Industry,
                Round0(profile?.MarketCapMillions),
                profile?.IpoDate?.ToString("yyyy-MM-dd"),
                StripAndTruncate(description, MaxDescriptionChars)),
            Price(quote, metrics),
            NullIfEmpty(evidence.Select(e => new BriefEvidence(e.Dimension, e.Status.ToString(),
                e.Observed, e.Threshold, e.AsOf, e.Note)).ToList()),
            NullIfEmpty(Fundamentals(metrics, sectorSummary, liveMetrics)),
            Health(sectorSummary),
            NullIfEmpty((recommendations ?? Array.Empty<RecommendationTrend>())
                .OrderByDescending(r => r.Period, StringComparer.Ordinal)
                .Take(MaxAnalystRecPeriods)
                .Select(r => new BriefRecTrend(r.Period, r.StrongBuy, r.Buy, r.Hold,
                    r.Sell, r.StrongSell,
                    BuyTotal: r.StrongBuy + r.Buy,
                    Total: r.StrongBuy + r.Buy + r.Hold + r.Sell + r.StrongSell))
                .ToList()),
            NullIfEmpty((earnings ?? Array.Empty<EarningsQuarter>())
                .OrderByDescending(e => e.Period, StringComparer.Ordinal)
                .Take(MaxEarningsQuarters)
                .Select(e => new BriefEarningsQuarter(e.Period, Round2(e.Estimate),
                    Round2(e.Actual), Round1(e.SurprisePercent)))
                .ToList()),
            EarningsRecord(earnings),
            nextEarningsDate?.ToString("yyyy-MM-dd"),
            // only the latest firing per signal kind: older firings carry
            // stale crawl-vintage numbers that read as a false trend
            NullIfEmpty(signals.GroupBy(s => s.Hit.SignalKind)
                .Select(g => g.OrderByDescending(x => x.Hit.FiredAt).First())
                .OrderByDescending(s => s.Hit.FiredAt)
                .Select(s => new BriefSignal(s.Hit.SignalKind,
                    s.Hit.FiredAt.ToString("yyyy-MM-dd"), s.Why.Summary,
                    s.Why.Conditions.Select(c => new BriefCondition(
                        c.Label, c.Observed, c.Threshold, c.Pass)).ToList()))
                .ToList()),
            returnHistory is null ? null : new BriefReturnHistory(
                returnHistory.WindowsAnalyzed, returnHistory.PctReturnedAtLeast10,
                returnHistory.MedianReturnPct, returnHistory.BestReturnPct,
                returnHistory.WorstReturnPct, returnHistory.From, returnHistory.To),
            NullIfEmpty(SignalRecords(signalRecords)),
            portfolioFitLine,
            NullIfEmpty(Headlines(ticker, profile?.Name, news)));
    }

    private static List<T>? NullIfEmpty<T>(List<T> list) => list.Count == 0 ? null : list;

    private static BriefEarningsRecord? EarningsRecord(IReadOnlyList<EarningsQuarter>? earnings)
    {
        var judged = (earnings ?? Array.Empty<EarningsQuarter>())
            .OrderByDescending(e => e.Period, StringComparer.Ordinal)
            .Take(MaxEarningsQuarters)
            .Where(e => e.SurprisePercent is not null)
            .ToList();
        if (judged.Count == 0) return null;
        return new BriefEarningsRecord(
            judged.Count(e => e.SurprisePercent > 0m),
            judged.Count(e => e.SurprisePercent < 0m),
            judged.Count);
    }

    private static List<BriefSignalRecord> SignalRecords(IReadOnlyList<ScoreboardRow>? rows)
    {
        if (rows is null) return new List<BriefSignalRecord>();
        return rows
            .Select(r =>
            {
                var h60 = r.Horizons.FirstOrDefault(h => h.Days == 60);
                return new BriefSignalRecord(r.SignalKind, h60?.Matured ?? 0,
                    Round1(h60?.BeatRatePct), Round1(h60?.MedianExcess));
            })
            .Where(r => r.Matured60d > 0)
            .ToList();
    }

    private static BriefPrice? Price(QuoteDetail? quote, TickerMetricsRow? m)
    {
        var current = quote?.Current;
        if (current is null && m?.Wk52High is null && m?.Wk52Low is null) return null;
        decimal? pctOff = current is decimal c && m?.Wk52High is decimal h && h > 0m
            ? Math.Round((h - c) / h * 100m, 1)
            : null;
        return new BriefPrice(Round2(current), Round2(m?.Wk52High), Round2(m?.Wk52Low), pctOff);
    }

    private static BriefSectorHealth? Health(SectorSummary? s)
    {
        if (s is null) return null;
        return new BriefSectorHealth(
            SectorHealth.Classify(s.BreadthAboveSma50, s.MedianReturn20d)?.ToString(),
            Round1(s.BreadthAboveSma50), Round1(s.BreadthAboveSma200),
            Round1(s.MedianReturn20d));
    }

    private static List<BriefMetric> Fundamentals(TickerMetricsRow? m, SectorSummary? s,
        FundamentalMetrics? live)
    {
        var rows = new List<BriefMetric>();
        if (m is not null)
        {
            rows.Add(Metric("P/E (TTM)", m.Pe, s?.MedianPe));
            rows.Add(Metric("P/S", m.Ps, s?.MedianPs));
            rows.Add(Metric("P/B", m.Pb, s?.MedianPb));
            rows.Add(Metric("Revenue growth YoY %", m.RevenueGrowthYoY, s?.MedianRevenueGrowth));
            rows.Add(Metric("EPS growth YoY %", m.EpsGrowth, null));
            rows.Add(Metric("Gross margin %", m.GrossMargin, null));
            rows.Add(Metric("Net margin %", m.NetMargin, null));
            rows.Add(Metric("ROE %", m.Roe, null));
            rows.Add(Metric("Beta", m.Beta, null));
        }
        // leverage and payout come only from the live metric snapshot — the
        // warehouse doesn't carry them (yet); a utility brief without a debt
        // or dividend line is how the VST memo ended up asking about leverage
        if (live is not null)
        {
            rows.Add(Metric("Dividend yield %", live.DividendYield, null));
            rows.Add(Metric("Debt/equity", live.DebtToEquity, null));
            rows.Add(Metric("Current ratio", live.CurrentRatio, null));
        }
        return rows.Where(r => r.Value is not null).ToList();
    }

    private static BriefMetric Metric(string name, decimal? value, decimal? median) =>
        new(name, Round2(value), Round2(median), Ratio(value, median));

    /// The multiple of the sector median, precomputed so the model can quote
    /// it instead of inventing arithmetic. Skipped for non-positive medians,
    /// where a multiple has no readable meaning.
    private static decimal? Ratio(decimal? value, decimal? median) =>
        value is decimal v && median is decimal m && m > 0m
            ? Math.Round(v / m, 2)
            : null;

    private static List<BriefHeadline> Headlines(string ticker, string? companyName,
        IReadOnlyList<NewsItem> news)
    {
        var name = CoreCompanyName(companyName);
        return news
            .Where(n => Mentions(n, ticker, name))
            .OrderByDescending(n => n.PublishedAt)
            .Take(MaxHeadlines)
            .Select(n => new BriefHeadline(n.PublishedAt.ToString("yyyy-MM-dd"),
                n.Source, n.Headline, TruncateSummary(n.Summary)))
            .ToList();
    }

    /// A headline must mention the ticker (cased, so "EXE" never matches
    /// "exe"), the company's core name, or its capitalized first word
    /// ("Micron beats…" rarely spells out "Micron Technology") — the
    /// provider's company feed mixes in peer and market stories.
    private static bool Mentions(NewsItem n, string ticker, string? coreName)
    {
        var text = n.Headline + " " + n.Summary;
        if (Regex.IsMatch(text, $@"(?<![A-Za-z]){Regex.Escape(ticker)}(?![A-Za-z])"))
            return true;
        if (coreName is null) return false;
        if (text.Contains(coreName, StringComparison.OrdinalIgnoreCase)) return true;
        // first word alone only when distinctive, and only capitalized as a
        // name — a lowercase "expand" is a verb, not Expand Energy
        var first = coreName.Split(' ')[0];
        return first.Length >= 4
               && Regex.IsMatch(text, $@"(?<![A-Za-z]){Regex.Escape(first)}(?![A-Za-z])");
    }

    private static readonly HashSet<string> NameSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "corp", "corporation", "inc", "incorporated", "ltd", "limited", "plc",
        "co", "company", "holdings", "holding", "group", "sa", "nv", "ag",
    };

    /// "Expand Energy Corp" -> "Expand Energy": headlines rarely carry the
    /// legal suffix, so matching on the full registered name finds nothing.
    private static string? CoreCompanyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(w => !NameSuffixes.Contains(w.TrimEnd('.', ',')))
            .ToList();
        return words.Count > 0 ? string.Join(' ', words) : name.Trim();
    }

    private static string? TruncateSummary(string? summary) =>
        StripAndTruncate(summary, MaxHeadlineSummaryChars);

    /// The payload guarantees no URLs reach the model; provider summaries and
    /// descriptions sometimes embed them.
    private static string? StripAndTruncate(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = Regex.Replace(text, @"https?://\S+", "").Trim();
        if (text.Length == 0) return null;
        return text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";
    }

    private static decimal? Round0(decimal? value) =>
        value is decimal v ? Math.Round(v, 0) : null;

    private static decimal? Round1(decimal? value) =>
        value is decimal v ? Math.Round(v, 1) : null;

    private static decimal? Round2(decimal? value) =>
        value is decimal v ? Math.Round(v, 2) : null;

    public static string Serialize(BriefPayload payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            // absent beats empty: a small model told about an empty list
            // writes filler bullets about the absence
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
}
