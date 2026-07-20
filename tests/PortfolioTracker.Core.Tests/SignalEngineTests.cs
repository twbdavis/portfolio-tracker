using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Tests;

public class SignalEngineTests
{
    private static readonly DateOnly Today = new(2026, 7, 20);
    private static readonly SignalSettings Defaults = new();

    private static TickerMetricsRow Metrics(string ticker, decimal? pe = null, decimal? growth = null,
        decimal? net = null, decimal? roe = null, decimal? high52 = null) => new()
    {
        Ticker = ticker, AsOf = DateTimeOffset.Now, Pe = pe, RevenueGrowthYoY = growth,
        NetMargin = net, Roe = roe, Wk52High = high52,
    };

    private static List<DailyCloseRow> Closes(string ticker, params decimal[] closes)
    {
        var date = new DateOnly(2025, 1, 2);
        var list = new List<DailyCloseRow>();
        foreach (var c in closes)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
            list.Add(new DailyCloseRow { Ticker = ticker, Date = date, Close = c });
            date = date.AddDays(1);
        }
        return list;
    }

    private static SignalInputs Inputs(
        (string Ticker, string Sector, TickerMetricsRow? Metrics, decimal[] Closes)[] tickers,
        Dictionary<string, List<RecommendationSnapshotRow>>? recs = null,
        Dictionary<string, EarningsInfo>? earnings = null)
    {
        var closesByTicker = tickers.Where(t => t.Closes.Length > 0)
            .ToDictionary(t => t.Ticker, t => Closes(t.Ticker, t.Closes), StringComparer.OrdinalIgnoreCase);
        var groups = SectorAnalyzer.GroupBySector(
            tickers.Select(t => t.Ticker).ToList(),
            tickers.Select(t => new TickerClassification
            {
                Ticker = t.Ticker, Sector = t.Sector, UpdatedAt = DateTimeOffset.Now,
            }).ToList(),
            tickers.Where(t => t.Metrics is not null).Select(t => t.Metrics!).ToList(),
            closesByTicker);
        return new SignalInputs(groups,
            recs ?? new Dictionary<string, List<RecommendationSnapshotRow>>(StringComparer.OrdinalIgnoreCase),
            closesByTicker,
            earnings ?? new Dictionary<string, EarningsInfo>(StringComparer.OrdinalIgnoreCase));
    }

    private static List<SignalCandidate> Fired(SignalInputs inputs) =>
        SignalEngine.Evaluate(inputs, Defaults, Today);

    // ---- CheapGrowth --------------------------------------------------------

    [Fact]
    public void CheapGrowth_fires_for_outlier_cheap_grower()
    {
        // CHEAP is far below the sector's PE spread with median growth
        var hits = Fired(Inputs(new[]
        {
            ("CHEAP", "S", (TickerMetricsRow?)Metrics("CHEAP", pe: 8m, growth: 10m), Array.Empty<decimal>()),
            ("A", "S", Metrics("A", pe: 30m, growth: 10m), Array.Empty<decimal>()),
            ("B", "S", Metrics("B", pe: 31m, growth: 8m), Array.Empty<decimal>()),
            ("C", "S", Metrics("C", pe: 29m, growth: 12m), Array.Empty<decimal>()),
            ("D", "S", Metrics("D", pe: 33m, growth: 9m), Array.Empty<decimal>()),
        }));
        var cheap = hits.Where(h => h.Kind == SignalKinds.CheapGrowth).ToList();
        Assert.Single(cheap);
        Assert.Equal("CHEAP", cheap[0].Ticker);
        Assert.All(cheap[0].Why.Conditions, c => Assert.True(c.Pass));
        Assert.Equal("S", cheap[0].Why.Sector);
    }

    [Fact]
    public void CheapGrowth_needs_growth_at_or_above_sector_median()
    {
        var hits = Fired(Inputs(new[]
        {
            ("CHEAP", "S", (TickerMetricsRow?)Metrics("CHEAP", pe: 8m, growth: -20m), Array.Empty<decimal>()),
            ("A", "S", Metrics("A", pe: 30m, growth: 10m), Array.Empty<decimal>()),
            ("B", "S", Metrics("B", pe: 31m, growth: 8m), Array.Empty<decimal>()),
            ("C", "S", Metrics("C", pe: 29m, growth: 12m), Array.Empty<decimal>()),
            ("D", "S", Metrics("D", pe: 33m, growth: 9m), Array.Empty<decimal>()),
        }));
        Assert.DoesNotContain(hits, h => h.Kind == SignalKinds.CheapGrowth);
    }

    [Fact]
    public void Thin_sector_fires_nothing_z_based()
    {
        var hits = Fired(Inputs(new[]
        {
            ("A", "S", (TickerMetricsRow?)Metrics("A", pe: 8m, growth: 10m), Array.Empty<decimal>()),
            ("B", "S", Metrics("B", pe: 30m, growth: 5m), Array.Empty<decimal>()),
        }));
        Assert.DoesNotContain(hits, h => h.Kind is SignalKinds.CheapGrowth or SignalKinds.QualityOnSale);
    }

    // ---- QualityOnSale ------------------------------------------------------

    [Fact]
    public void QualityOnSale_fires_for_beaten_down_quality()
    {
        // SALE: deepest off-high, best growth/margin/ROE; peers healthy near highs
        (string, string, TickerMetricsRow?, decimal[])[] tickers =
        {
            ("SALE", "S", (TickerMetricsRow?)Metrics("SALE", growth: 30m, net: 25m, roe: 35m, high52: 200m),
                new[] { 100m, 100m, 100m, 100m, 100m, 100m }),   // 50% off high
            ("A", "S", Metrics("A", growth: 5m, net: 10m, roe: 10m, high52: 100m),
                new[] { 95m, 95m, 95m, 95m, 95m, 95m }),
            ("B", "S", Metrics("B", growth: 6m, net: 11m, roe: 11m, high52: 100m),
                new[] { 96m, 96m, 96m, 96m, 96m, 96m }),
            ("C", "S", Metrics("C", growth: 4m, net: 9m, roe: 12m, high52: 100m),
                new[] { 97m, 97m, 97m, 97m, 97m, 97m }),
            ("D", "S", Metrics("D", growth: 5m, net: 12m, roe: 9m, high52: 100m),
                new[] { 94m, 94m, 94m, 94m, 94m, 94m }),
        };
        var hits = Fired(Inputs(tickers));
        var sale = hits.Where(h => h.Kind == SignalKinds.QualityOnSale).ToList();
        Assert.Single(sale);
        Assert.Equal("SALE", sale[0].Ticker);
    }

    [Fact]
    public void QualityOnSale_needs_at_least_two_fundamental_zs()
    {
        // SALE has only growth (1 of 3 z inputs): composite unavailable -> no fire
        (string, string, TickerMetricsRow?, decimal[])[] tickers =
        {
            ("SALE", "S", Metrics("SALE", growth: 30m, high52: 200m),
                new[] { 100m, 100m, 100m, 100m, 100m, 100m }),
            ("A", "S", Metrics("A", growth: 5m, net: 10m, roe: 10m, high52: 100m),
                new[] { 95m, 95m, 95m, 95m, 95m, 95m }),
            ("B", "S", Metrics("B", growth: 6m, net: 11m, roe: 11m, high52: 100m),
                new[] { 96m, 96m, 96m, 96m, 96m, 96m }),
            ("C", "S", Metrics("C", growth: 4m, net: 9m, roe: 12m, high52: 100m),
                new[] { 97m, 97m, 97m, 97m, 97m, 97m }),
        };
        Assert.DoesNotContain(Fired(Inputs(tickers)), h => h.Kind == SignalKinds.QualityOnSale);
    }

    // ---- RecMomentum --------------------------------------------------------

    private static List<RecommendationSnapshotRow> Recs(string ticker, params (string Month, int Sb, int B, int H)[] months) =>
        months.Select(m => new RecommendationSnapshotRow
        {
            Ticker = ticker, Month = m.Month, StrongBuy = m.Sb, Buy = m.B, Hold = m.H,
        }).OrderByDescending(r => r.Month).ToList();

    [Fact]
    public void RecMomentum_fires_on_two_rising_months_with_coverage()
    {
        var recs = new Dictionary<string, List<RecommendationSnapshotRow>>(StringComparer.OrdinalIgnoreCase)
        {
            // buy ratio 10/20 -> 12/20 -> 14/20
            ["NVDA"] = Recs("NVDA", ("2026-05-01", 5, 5, 10), ("2026-06-01", 6, 6, 8), ("2026-07-01", 7, 7, 6)),
        };
        var hits = Fired(Inputs(new[]
        {
            ("NVDA", "S", (TickerMetricsRow?)null, Array.Empty<decimal>()),
        }, recs));
        var hit = hits.Single(h => h.Kind == SignalKinds.RecMomentum);
        Assert.Equal("NVDA", hit.Ticker);
    }

    [Fact]
    public void RecMomentum_respects_min_analysts_and_needs_three_snapshots()
    {
        var thin = new Dictionary<string, List<RecommendationSnapshotRow>>(StringComparer.OrdinalIgnoreCase)
        {
            ["TINY"] = Recs("TINY", ("2026-05-01", 0, 1, 3), ("2026-06-01", 1, 1, 2), ("2026-07-01", 2, 1, 1)),
            ["SHORT"] = Recs("SHORT", ("2026-06-01", 5, 5, 5), ("2026-07-01", 8, 5, 2)),
        };
        var hits = Fired(Inputs(new[]
        {
            ("TINY", "S", (TickerMetricsRow?)null, Array.Empty<decimal>()),
            ("SHORT", "S", (TickerMetricsRow?)null, Array.Empty<decimal>()),
        }, thin));
        Assert.DoesNotContain(hits, h => h.Kind == SignalKinds.RecMomentum);
    }

    // ---- Golden/Death cross -------------------------------------------------

    [Fact]
    public void GoldenCross_fires_and_insufficient_history_stays_quiet()
    {
        var goldenSeries = Enumerable.Repeat(150m, 200)
            .Concat(Enumerable.Repeat(100m, 55))
            .Concat(Enumerable.Repeat(200m, 23)).ToArray();
        var hits = Fired(Inputs(new[]
        {
            ("GOLD", "S", (TickerMetricsRow?)null, goldenSeries),
            ("SHORT", "S", (TickerMetricsRow?)null, Enumerable.Repeat(100m, 100).ToArray()),
        }));
        Assert.Contains(hits, h => h.Kind == SignalKinds.GoldenCross && h.Ticker == "GOLD");
        Assert.DoesNotContain(hits, h => h.Ticker == "SHORT"
            && h.Kind is SignalKinds.GoldenCross or SignalKinds.DeathCross);
    }

    // ---- SectorDivergence ---------------------------------------------------

    [Fact]
    public void SectorDivergence_fires_for_big_laggard_only()
    {
        static decimal[] Flat(decimal v) => Enumerable.Repeat(v, 31).ToArray();
        static decimal[] Move(decimal from, decimal to) =>
            new[] { from }.Concat(Enumerable.Repeat(to, 30)).ToArray();

        var hits = Fired(Inputs(new[]
        {
            ("LAG", "S", (TickerMetricsRow?)null, Move(100m, 80m)),    // -20%
            ("A", "S", (TickerMetricsRow?)null, Flat(100m)),           // 0%
            ("B", "S", (TickerMetricsRow?)null, Move(100m, 102m)),     // +2%
            ("C", "S", (TickerMetricsRow?)null, Move(100m, 98m)),      // -2%
        }));
        var d = hits.Where(h => h.Kind == SignalKinds.SectorDivergence).ToList();
        Assert.Single(d);
        Assert.Equal("LAG", d[0].Ticker);
    }

    // ---- CatalystWatch ------------------------------------------------------

    private static EarningsInfo Beats(DateOnly? next, params bool[] beats) => new(
        beats.Select((b, i) => new EarningsQuarter
        {
            Period = $"2026-0{4 - i}-01",
            Estimate = 1m,
            Actual = b ? 1.1m : 0.9m,
        }).ToList(), next);

    [Fact]
    public void CatalystWatch_fires_on_four_beats_and_imminent_earnings()
    {
        var earnings = new Dictionary<string, EarningsInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOT"] = Beats(Today.AddDays(10), true, true, true, true),
            ["MISS"] = Beats(Today.AddDays(10), true, false, true, true),
            ["FAR"] = Beats(Today.AddDays(30), true, true, true, true),
            ["PAST"] = Beats(Today.AddDays(-2), true, true, true, true),
        };
        var hits = Fired(Inputs(new[]
        {
            ("HOT", "S", (TickerMetricsRow?)null, Array.Empty<decimal>()),
            ("MISS", "S", (TickerMetricsRow?)null, Array.Empty<decimal>()),
            ("FAR", "S", (TickerMetricsRow?)null, Array.Empty<decimal>()),
            ("PAST", "S", (TickerMetricsRow?)null, Array.Empty<decimal>()),
        }, earnings: earnings));
        var c = hits.Where(h => h.Kind == SignalKinds.CatalystWatch).ToList();
        Assert.Single(c);
        Assert.Equal("HOT", c[0].Ticker);
    }

    // ---- BreadthThrust ------------------------------------------------------

    [Fact]
    public void BreadthThrust_fires_when_sector_breadth_recovers()
    {
        // 5 tickers, 61 bars: long decline (below SMA50), then a rally in the
        // last few sessions pushes all above SMA50 — breadth 0% -> 100%
        static decimal[] Thrust() =>
            Enumerable.Range(0, 55).Select(i => 200m - i).Concat(Enumerable.Repeat(400m, 8)).ToArray();

        var tickers = Enumerable.Range(1, 5)
            .Select(i => ($"T{i}", "S", (TickerMetricsRow?)null, Thrust()))
            .ToArray();
        var hits = Fired(Inputs(tickers));
        var thrust = hits.Where(h => h.Kind == SignalKinds.BreadthThrust).ToList();
        Assert.Single(thrust);
        Assert.Equal("S", thrust[0].Ticker);   // sector-level: ticker = sector name
    }

    [Fact]
    public void BreadthThrust_skips_thin_sectors_and_steady_breadth()
    {
        static decimal[] Rising() =>
            Enumerable.Range(0, 63).Select(i => 100m + i).ToArray();   // always above SMA50

        // thin: 3 tickers < MinTickers
        var thin = Enumerable.Range(1, 3)
            .Select(i => ($"T{i}", "S", (TickerMetricsRow?)null, Rising()))
            .ToArray();
        Assert.DoesNotContain(Fired(Inputs(thin)), h => h.Kind == SignalKinds.BreadthThrust);

        // wide but steady-high breadth: no thrust
        var steady = Enumerable.Range(1, 6)
            .Select(i => ($"T{i}", "S", (TickerMetricsRow?)null, Rising()))
            .ToArray();
        Assert.DoesNotContain(Fired(Inputs(steady)), h => h.Kind == SignalKinds.BreadthThrust);
    }

    // ---- cross-cutting --------------------------------------------------------

    [Fact]
    public void Unclassified_tickers_never_fire()
    {
        var hits = Fired(Inputs(new[]
        {
            ("MYSTERY", SectorAnalyzer.Unclassified, (TickerMetricsRow?)Metrics("MYSTERY", pe: 1m, growth: 100m),
                Array.Empty<decimal>()),
        }));
        Assert.Empty(hits);
    }

    [Fact]
    public void Every_hit_carries_a_bounded_strength()
    {
        var goldenSeries = Enumerable.Repeat(150m, 200)
            .Concat(Enumerable.Repeat(100m, 55))
            .Concat(Enumerable.Repeat(200m, 23)).ToArray();
        var hits = Fired(Inputs(new[]
        {
            ("CHEAP", "S", (TickerMetricsRow?)Metrics("CHEAP", pe: 8m, growth: 10m), Array.Empty<decimal>()),
            ("A", "S", Metrics("A", pe: 30m, growth: 10m), Array.Empty<decimal>()),
            ("B", "S", Metrics("B", pe: 31m, growth: 8m), Array.Empty<decimal>()),
            ("C", "S", Metrics("C", pe: 29m, growth: 12m), Array.Empty<decimal>()),
            ("GOLD", "S", null, goldenSeries),
        }));
        Assert.NotEmpty(hits);
        Assert.All(hits, h =>
        {
            Assert.NotNull(h.Why.Strength);
            Assert.InRange(h.Why.Strength!.Value, 0, 100);
        });
    }

    [Fact]
    public void Strength_ranks_a_deeper_divergence_above_a_marginal_one()
    {
        static decimal[] Move(decimal from, decimal to) =>
            new[] { from }.Concat(Enumerable.Repeat(to, 30)).ToArray();

        var hits = Fired(Inputs(new[]
        {
            ("DEEP", "S", (TickerMetricsRow?)null, Move(100m, 60m)),     // -40%: far past the 15pp gap
            ("MILD", "S", (TickerMetricsRow?)null, Move(100m, 84m)),     // -16%: barely past
            ("A", "S", (TickerMetricsRow?)null, Move(100m, 101m)),
            ("B", "S", (TickerMetricsRow?)null, Move(100m, 100m)),
            ("C", "S", (TickerMetricsRow?)null, Move(100m, 99m)),
        }));
        var divergence = hits.Where(h => h.Kind == SignalKinds.SectorDivergence)
            .ToDictionary(h => h.Ticker, h => h.Why.Strength!.Value);
        Assert.True(divergence["DEEP"] > divergence["MILD"],
            $"expected DEEP ({divergence["DEEP"]}) > MILD ({divergence["MILD"]})");
    }

    [Fact]
    public void Strength_ranks_a_bigger_cheapness_margin_higher()
    {
        static SignalInputs Sector(decimal cheapPe) => Inputs(new[]
        {
            ("CHEAP", "S", (TickerMetricsRow?)Metrics("CHEAP", pe: cheapPe, growth: 10m), Array.Empty<decimal>()),
            ("A", "S", Metrics("A", pe: 30m, growth: 10m), Array.Empty<decimal>()),
            ("B", "S", Metrics("B", pe: 31m, growth: 8m), Array.Empty<decimal>()),
            ("C", "S", Metrics("C", pe: 29m, growth: 12m), Array.Empty<decimal>()),
            ("D", "S", Metrics("D", pe: 33m, growth: 9m), Array.Empty<decimal>()),
        });
        int StrengthOf(SignalInputs i) => Fired(i)
            .Single(h => h.Kind == SignalKinds.CheapGrowth && h.Ticker == "CHEAP").Why.Strength!.Value;
        // pe=28 fires marginally (z ≈ 1.35); pe=4 is a blowout (z saturates)
        Assert.True(StrengthOf(Sector(4m)) > StrengthOf(Sector(28m)));
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var inputs = Inputs(new[]
        {
            ("CHEAP", "S", (TickerMetricsRow?)Metrics("CHEAP", pe: 8m, growth: 10m), Array.Empty<decimal>()),
            ("A", "S", Metrics("A", pe: 30m, growth: 10m), Array.Empty<decimal>()),
            ("B", "S", Metrics("B", pe: 31m, growth: 8m), Array.Empty<decimal>()),
            ("C", "S", Metrics("C", pe: 29m, growth: 12m), Array.Empty<decimal>()),
        });
        var a = Fired(inputs).Select(h => (h.Ticker, h.Kind, h.Why.Summary)).ToList();
        var b = Fired(inputs).Select(h => (h.Ticker, h.Kind, h.Why.Summary)).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Every_fired_condition_passes_and_carries_arithmetic()
    {
        var hits = Fired(Inputs(new[]
        {
            ("CHEAP", "S", (TickerMetricsRow?)Metrics("CHEAP", pe: 8m, growth: 10m), Array.Empty<decimal>()),
            ("A", "S", Metrics("A", pe: 30m, growth: 10m), Array.Empty<decimal>()),
            ("B", "S", Metrics("B", pe: 31m, growth: 8m), Array.Empty<decimal>()),
            ("C", "S", Metrics("C", pe: 29m, growth: 12m), Array.Empty<decimal>()),
        }));
        Assert.NotEmpty(hits);
        Assert.All(hits, h =>
        {
            Assert.NotEmpty(h.Why.Summary);
            Assert.NotEmpty(h.Why.Conditions);
            Assert.All(h.Why.Conditions, c =>
            {
                Assert.True(c.Pass);
                Assert.NotEmpty(c.Observed);
                Assert.NotEmpty(c.Threshold);
            });
        });
    }
}

public class SignalSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pt-signals-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_writes_documented_defaults()
    {
        var path = Path.Combine(_dir, "signals.json");
        var s = SignalSettings.Load(path);
        Assert.Equal(14, s.DedupeDays);
        Assert.True(File.Exists(path));
        Assert.Contains("CheapGrowthPeZMin", File.ReadAllText(path));
    }

    [Fact]
    public void Edited_file_hot_loads_on_next_read()
    {
        var path = Path.Combine(_dir, "signals.json");
        SignalSettings.Load(path);
        var json = File.ReadAllText(path).Replace("\"DedupeDays\": 14", "\"DedupeDays\": 3");
        File.WriteAllText(path, json);
        Assert.Equal(3, SignalSettings.Load(path).DedupeDays);
    }

    [Fact]
    public void Corrupted_file_falls_back_to_defaults_without_clobbering()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "signals.json");
        File.WriteAllText(path, "{not json");
        var s = SignalSettings.Load(path);
        Assert.Equal(14, s.DedupeDays);
        Assert.Equal("{not json", File.ReadAllText(path));   // user's file left alone
    }
}
