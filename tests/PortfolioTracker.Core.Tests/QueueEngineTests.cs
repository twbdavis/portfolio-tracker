using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Tests;

public class QueueEngineTests
{
    private static readonly DateOnly Today = new(2026, 8, 1);
    private static readonly QueueSettings Defaults = new();

    private static TickerMetricsRow Metrics(string ticker, decimal? pe = null, decimal? ps = null,
        decimal? pb = null, decimal? growth = null, decimal? margin = null, decimal? roe = null) => new()
    {
        Ticker = ticker, AsOf = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero),
        Pe = pe, Ps = ps, Pb = pb, RevenueGrowthYoY = growth, NetMargin = margin, Roe = roe,
    };

    /// A one-sector universe with enough members for robust stats. CHEAP is
    /// cheapest and highest quality; DEAR is the opposite; MID* fill the
    /// population.
    private static Dictionary<string, List<TickerAnalysis>> Groups(params TickerMetricsRow[] rows) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Tech"] = rows.Select(m => new TickerAnalysis(m.Ticker, "Tech", m,
                PriceDerived.Compute(new List<DailyCloseRow>()))).ToList(),
        };

    private static TickerMetricsRow[] StandardSector() => new[]
    {
        Metrics("CHEAP", pe: 8m, ps: 1m, pb: 1m, growth: 30m, margin: 25m, roe: 30m),
        Metrics("MID1", pe: 20m, ps: 3m, pb: 4m, growth: 10m, margin: 10m, roe: 12m),
        Metrics("MID2", pe: 22m, ps: 3.5m, pb: 4.5m, growth: 9m, margin: 9m, roe: 11m),
        Metrics("MID3", pe: 24m, ps: 4m, pb: 5m, growth: 8m, margin: 8m, roe: 10m),
        Metrics("DEAR", pe: 60m, ps: 12m, pb: 15m, growth: -5m, margin: 1m, roe: 2m),
    };

    private static QueueInputs Inputs(
        IReadOnlyCollection<string>? candidates = null,
        Dictionary<string, List<TickerAnalysis>>? groups = null,
        Dictionary<string, List<string>>? signals = null,
        Dictionary<string, SectorQuadrant?>? quadrants = null,
        Dictionary<string, EarningsInfo>? earnings = null,
        Dictionary<string, PortfolioFitRow>? fit = null,
        Dictionary<string, string>? downWeighted = null)
    {
        groups ??= Groups(StandardSector());
        return new QueueInputs(
            candidates ?? groups.Values.SelectMany(g => g).Select(t => t.Ticker).ToList(),
            groups,
            signals ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
            quadrants ?? new Dictionary<string, SectorQuadrant?>(StringComparer.OrdinalIgnoreCase),
            earnings ?? new Dictionary<string, EarningsInfo>(StringComparer.OrdinalIgnoreCase),
            fit ?? new Dictionary<string, PortfolioFitRow>(StringComparer.OrdinalIgnoreCase),
            downWeighted ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private static EvidenceItem Dim(QueueCandidate c, string dimension) =>
        c.Evidence.Single(e => e.Dimension == dimension);

    // ---- n/a handling ------------------------------------------------------------

    [Fact]
    public void Na_dimensions_are_excluded_from_the_denominator_never_failed()
    {
        var ranked = QueueEngine.Rank(Inputs(), Defaults, Today);
        var cheap = ranked.Single(c => c.Ticker == "CHEAP");

        // no earnings cache, no fit rows -> catalyst and fit are n/a
        Assert.Equal(EvidenceStatus.NotAvailable, Dim(cheap, QueueDimensions.Catalyst).Status);
        Assert.Equal(EvidenceStatus.NotAvailable, Dim(cheap, QueueDimensions.PortfolioFit).Status);
        // sector health has no quadrant either -> n/a
        Assert.Equal(EvidenceStatus.NotAvailable, Dim(cheap, QueueDimensions.SectorHealth).Status);

        // denominator = signals(1) + relative value(1) + quality(1) only
        Assert.Equal(3m, cheap.ApplicableWeight);
        // CHEAP passes both tercile dimensions, fails signals -> 2/3
        Assert.Equal(2m, cheap.WeightedPasses);
        Assert.Equal(Math.Round(2m / 3m, 4), Math.Round(cheap.Score, 4));
    }

    [Fact]
    public void Ticker_without_metrics_gets_na_composites_not_failures()
    {
        var rows = StandardSector().Append(Metrics("ETF")).ToArray();   // no fundamentals at all
        var ranked = QueueEngine.Rank(Inputs(groups: Groups(rows)), Defaults, Today);
        var etf = ranked.Single(c => c.Ticker == "ETF");
        Assert.Equal(EvidenceStatus.NotAvailable, Dim(etf, QueueDimensions.RelativeValue).Status);
        Assert.Equal(EvidenceStatus.NotAvailable, Dim(etf, QueueDimensions.Quality).Status);
        Assert.Contains("ETF", Dim(etf, QueueDimensions.RelativeValue).Observed);
    }

    [Fact]
    public void Unclassified_ticker_has_na_sector_dimensions()
    {
        var groups = Groups(StandardSector());
        groups[SectorAnalyzer.Unclassified] = new List<TickerAnalysis>
        {
            new("MYST", SectorAnalyzer.Unclassified, Metrics("MYST", pe: 5m),
                PriceDerived.Compute(new List<DailyCloseRow>())),
        };
        var ranked = QueueEngine.Rank(Inputs(groups: groups), Defaults, Today);
        var myst = ranked.Single(c => c.Ticker == "MYST");
        Assert.Equal(EvidenceStatus.NotAvailable, Dim(myst, QueueDimensions.SectorHealth).Status);
        Assert.Equal(EvidenceStatus.NotAvailable, Dim(myst, QueueDimensions.RelativeValue).Status);
        Assert.Equal(EvidenceStatus.NotAvailable, Dim(myst, QueueDimensions.Quality).Status);
    }

    // ---- dimension rules -----------------------------------------------------------

    [Fact]
    public void Signals_dimension_counts_each_kind_once()
    {
        var signals = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHEAP"] = new() { "CheapGrowth", "CheapGrowth", "GoldenCross" },
        };
        var ranked = QueueEngine.Rank(Inputs(signals: signals), Defaults, Today);
        var e = Dim(ranked.Single(c => c.Ticker == "CHEAP"), QueueDimensions.Signals);
        Assert.Equal(EvidenceStatus.Pass, e.Status);
        Assert.StartsWith("2 kinds:", e.Observed);
    }

    [Fact]
    public void Sector_health_quadrants_map_to_pass_neutral_fail()
    {
        foreach (var (quadrant, expected) in new (SectorQuadrant, EvidenceStatus)[]
        {
            (SectorQuadrant.HealthyRally, EvidenceStatus.Pass),
            (SectorQuadrant.Recovering, EvidenceStatus.Pass),
            (SectorQuadrant.Topping, EvidenceStatus.Neutral),
            (SectorQuadrant.Breakdown, EvidenceStatus.Fail),
        })
        {
            var quadrants = new Dictionary<string, SectorQuadrant?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tech"] = quadrant,
            };
            var ranked = QueueEngine.Rank(Inputs(quadrants: quadrants), Defaults, Today);
            Assert.Equal(expected,
                Dim(ranked.Single(c => c.Ticker == "CHEAP"), QueueDimensions.SectorHealth).Status);
        }
    }

    [Fact]
    public void Neutral_counts_in_denominator_but_not_numerator()
    {
        var quadrants = new Dictionary<string, SectorQuadrant?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tech"] = SectorQuadrant.Topping,
        };
        var ranked = QueueEngine.Rank(Inputs(quadrants: quadrants), Defaults, Today);
        var cheap = ranked.Single(c => c.Ticker == "CHEAP");
        Assert.Equal(4m, cheap.ApplicableWeight);   // signals + sector health + two terciles
        Assert.Equal(2m, cheap.WeightedPasses);     // terciles only
    }

    [Fact]
    public void Relative_value_and_quality_pass_only_in_best_tercile()
    {
        var ranked = QueueEngine.Rank(Inputs(), Defaults, Today);
        var cheap = ranked.Single(c => c.Ticker == "CHEAP");
        var dear = ranked.Single(c => c.Ticker == "DEAR");
        Assert.Equal(EvidenceStatus.Pass, Dim(cheap, QueueDimensions.RelativeValue).Status);
        Assert.Equal(EvidenceStatus.Pass, Dim(cheap, QueueDimensions.Quality).Status);
        Assert.Equal(EvidenceStatus.Fail, Dim(dear, QueueDimensions.RelativeValue).Status);
        Assert.Equal(EvidenceStatus.Fail, Dim(dear, QueueDimensions.Quality).Status);
    }

    [Fact]
    public void Catalyst_requires_proximity_and_three_of_four_beats()
    {
        EarningsInfo Info(int daysOut, int beats)
        {
            var quarters = Enumerable.Range(0, 4).Select(i => new EarningsQuarter
            {
                Period = $"2026-0{4 - i}-01",
                Estimate = 1m,
                Actual = i < beats ? 1.2m : 0.8m,
            }).ToList();
            return new EarningsInfo(quarters, Today.AddDays(daysOut));
        }

        var earnings = new Dictionary<string, EarningsInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHEAP"] = Info(daysOut: 7, beats: 3),
            ["MID1"] = Info(daysOut: 7, beats: 2),    // beats short
            ["MID2"] = Info(daysOut: 30, beats: 4),   // too far out
        };
        var ranked = QueueEngine.Rank(Inputs(earnings: earnings), Defaults, Today);
        Assert.Equal(EvidenceStatus.Pass,
            Dim(ranked.Single(c => c.Ticker == "CHEAP"), QueueDimensions.Catalyst).Status);
        Assert.Equal(EvidenceStatus.Fail,
            Dim(ranked.Single(c => c.Ticker == "MID1"), QueueDimensions.Catalyst).Status);
        Assert.Equal(EvidenceStatus.Fail,
            Dim(ranked.Single(c => c.Ticker == "MID2"), QueueDimensions.Catalyst).Status);
    }

    [Fact]
    public void Portfolio_fit_passes_below_the_correlation_threshold_and_carries_extra_weight()
    {
        var fit = new Dictionary<string, PortfolioFitRow>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHEAP"] = new() { Ticker = "CHEAP", AsOf = DateTimeOffset.Now, Correlation = 0.2m, Observations = 80 },
            ["DEAR"] = new() { Ticker = "DEAR", AsOf = DateTimeOffset.Now, Correlation = 0.9m, Observations = 80 },
            ["MID1"] = new() { Ticker = "MID1", AsOf = DateTimeOffset.Now, Correlation = null, Observations = 12 },
        };
        var ranked = QueueEngine.Rank(Inputs(fit: fit), Defaults, Today);

        var cheap = Dim(ranked.Single(c => c.Ticker == "CHEAP"), QueueDimensions.PortfolioFit);
        Assert.Equal(EvidenceStatus.Pass, cheap.Status);
        Assert.Equal(1.5m, cheap.Weight);

        Assert.Equal(EvidenceStatus.Fail,
            Dim(ranked.Single(c => c.Ticker == "DEAR"), QueueDimensions.PortfolioFit).Status);
        // insufficient observations -> n/a, not fail
        var mid = Dim(ranked.Single(c => c.Ticker == "MID1"), QueueDimensions.PortfolioFit);
        Assert.Equal(EvidenceStatus.NotAvailable, mid.Status);
        Assert.Contains("12 sessions", mid.Observed);
    }

    // ---- weights, down-weighting, ordering ----------------------------------------------

    [Fact]
    public void Custom_weights_change_the_score()
    {
        var settings = new QueueSettings { WeightQuality = 3m };
        var ranked = QueueEngine.Rank(Inputs(), settings, Today);
        var cheap = ranked.Single(c => c.Ticker == "CHEAP");
        Assert.Equal(5m, cheap.ApplicableWeight);   // 1 + 1 + 3
        Assert.Equal(4m, cheap.WeightedPasses);     // relative value 1 + quality 3
    }

    [Fact]
    public void Down_weighted_signal_kinds_halve_their_credit_and_say_why()
    {
        var signals = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHEAP"] = new() { "DeathCross" },
        };
        var downWeighted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeathCross"] = "60d sector beat-rate 38% over 24 matured outcomes",
        };
        var ranked = QueueEngine.Rank(Inputs(signals: signals, downWeighted: downWeighted),
            Defaults, Today);
        var e = Dim(ranked.Single(c => c.Ticker == "CHEAP"), QueueDimensions.Signals);

        Assert.Equal(EvidenceStatus.Pass, e.Status);
        Assert.Equal(1m, e.Weight);
        Assert.Equal(0.5m, e.AppliedWeight);
        Assert.Contains("DeathCross", e.Note);
        Assert.Contains("38%", e.Note);

        // an unflagged kind alongside restores full credit
        signals["CHEAP"].Add("CheapGrowth");
        var ranked2 = QueueEngine.Rank(Inputs(signals: signals, downWeighted: downWeighted),
            Defaults, Today);
        Assert.Equal(1m,
            Dim(ranked2.Single(c => c.Ticker == "CHEAP"), QueueDimensions.Signals).AppliedWeight);
    }

    [Fact]
    public void Ties_break_by_fundamentals_rank()
    {
        var ranked = QueueEngine.Rank(Inputs(candidates: new[] { "MID1", "MID2", "MID3" }),
            Defaults, Today);
        // MID2 and MID3 tie at score 0 -> the higher fundamentals composite wins
        Assert.Equal(new[] { "MID1", "MID2", "MID3" }, ranked.Select(c => c.Ticker));
        Assert.True(ranked[0].FundamentalsRank >= ranked[1].FundamentalsRank);
        Assert.True(ranked[1].FundamentalsRank >= ranked[2].FundamentalsRank);
    }

    [Fact]
    public void Every_row_carries_all_six_dimensions_in_the_breakdown()
    {
        var ranked = QueueEngine.Rank(Inputs(), Defaults, Today);
        foreach (var c in ranked)
            Assert.Equal(new[]
            {
                QueueDimensions.Signals, QueueDimensions.SectorHealth,
                QueueDimensions.RelativeValue, QueueDimensions.Quality,
                QueueDimensions.Catalyst, QueueDimensions.PortfolioFit,
            }, c.Evidence.Select(e => e.Dimension));
    }
}
