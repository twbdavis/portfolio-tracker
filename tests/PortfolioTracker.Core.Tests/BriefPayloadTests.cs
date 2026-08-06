using System.Text.Json;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Tests;

/// The grounding contract: the serialized AI payload may contain ONLY the
/// whitelisted property names. If a field is ever added to the payload, it
/// must be added here consciously — that is the point of the test.
public class BriefPayloadTests
{
    private static readonly HashSet<string> Whitelist = new(StringComparer.Ordinal)
    {
        // BriefPayload
        "ticker", "profile", "price", "evidence", "fundamentals", "sectorHealth",
        "analystRecommendations", "earningsHistory", "signals", "twoMonthReturnHistory",
        "signalTrackRecord", "portfolioFit", "headlines",
        // BriefPayload scalar extras
        "nextEarningsDate",
        // BriefProfile
        "name", "sector", "industry", "marketCapMillions", "ipoDate", "description",
        // BriefPrice
        "current", "wk52High", "wk52Low", "pctOff52wHigh",
        // BriefEvidence
        "dimension", "status", "observed", "threshold", "asOf", "note",
        // BriefMetric
        "value", "sectorMedian", "xSectorMedian",
        // BriefSectorHealth
        "quadrant", "breadthAboveSma50Pct", "breadthAboveSma200Pct", "medianReturn20dPct",
        // BriefRecTrend
        "period", "strongBuy", "buy", "hold", "sell", "strongSell", "buyTotal", "total",
        // BriefEarningsRecord
        "earningsRecord", "beats", "misses", "quarters",
        // BriefEarningsQuarter
        "epsEstimate", "epsActual", "surprisePercent",
        // BriefReturnHistory
        "windowsAnalyzed", "pctReturnedAtLeast10", "medianReturnPct", "bestReturnPct",
        "worstReturnPct", "from", "to",
        // BriefSignalRecord
        "matured60d", "beatRate60dPct", "medianExcess60dPct",
        // BriefSignal
        "kind", "firedAt", "summary", "conditions",
        // BriefCondition
        "label", "pass",
        // BriefHeadline
        "date", "source", "title",
    };

    private static BriefPayload FullPayload()
    {
        var profile = new CompanyProfile
        {
            Ticker = "MU", Name = "Micron Technology Inc", Industry = "Semiconductors",
            MarketCapMillions = 120000m, IpoDate = new DateOnly(1984, 6, 1),
            WebUrl = "https://example.com",   // must NOT survive into the payload
        };
        var evidence = new List<EvidenceItem>
        {
            new(QueueDimensions.Quality, "Quality", EvidenceStatus.Pass,
                "z̄ = 1.2, 80th percentile", "≥ 67th", "2026-07-31", 1m, 1m, "note"),
        };
        var metrics = new TickerMetricsRow
        {
            Ticker = "MU", AsOf = DateTimeOffset.Now, Pe = 12m, Ps = 3m, Pb = 2m,
            RevenueGrowthYoY = 20m, EpsGrowth = 25m, GrossMargin = 40m, NetMargin = 20m,
            Roe = 15m, Beta = 1.2m, Wk52High = 120m, Wk52Low = 60m,
        };
        var summary = new SectorSummary("Technology", 10, 9, 9, 22m, 4m, 5m, 8m,
            12m, 60m, 55m, 2m, new List<TickerReturn>(), new List<TickerReturn>(), null, null);
        var hit = new SignalHit
        {
            Id = 1, Ticker = "MU", SignalKind = "CheapGrowth",
            FiredAt = DateTimeOffset.Now, InputsJson = "{}",
        };
        var why = new SignalWhy("cheap grower", "Technology",
            new List<SignalConditionResult> { new("P/E z", "1.4", "≥ 1.0", true) }, 70);
        var news = Enumerable.Range(0, 15).Select(i => new NewsItem
        {
            PublishedAt = DateTimeOffset.Now.AddDays(-i), Source = "Wire",
            Headline = $"Micron ships new part, day {i}",
            Url = "https://example.com/article",   // url must not leak
            Summary = i == 0 ? "Details at https://example.com/inline for the launch." : null,
        }).ToList();
        var quote = new QuoteDetail("MU", 95m, 94m, DateTimeOffset.Now);
        var recs = new List<RecommendationTrend>
        {
            new() { Period = "2026-06-01", StrongBuy = 5, Buy = 10, Hold = 8, Sell = 1, StrongSell = 0 },
            new() { Period = "2026-07-01", StrongBuy = 6, Buy = 11, Hold = 7, Sell = 1, StrongSell = 0 },
            new() { Period = "2026-05-01", StrongBuy = 4, Buy = 9, Hold = 9, Sell = 2, StrongSell = 1 },
        };
        var earnings = Enumerable.Range(0, 6).Select(i => new EarningsQuarter
        {
            Period = $"2026-0{6 - i}-30", Estimate = 1.0m + i, Actual = 1.1m + i,
            SurprisePercent = 2.5m,
        }).ToList();

        var returnHistory = new TwoMonthReturnStats(208, 34.1m, 4.2m, 38.9m, -21.5m,
            "2024-06-30", "2026-07-31");
        var signalRecords = new List<ScoreboardRow>
        {
            new("CheapGrowth", 30, 2, 6.7m, new List<ScoreboardHorizon>
            {
                new(30, 18, 1.2m, 55.6m, null, null, true),
                new(60, 24, 2.8m, 58.3m, null, null, true),
            }, false, null),
            new("NeverMatured", 3, 0, 0m, new List<ScoreboardHorizon>
            {
                new(60, 0, null, null, null, null, false),
            }, false, null),
        };

        var liveMetrics = new FundamentalMetrics
        {
            DividendYield = 2.14m, DebtToEquity = 1.457m, CurrentRatio = 0.92m,
        };

        return BriefPayloadBuilder.Build("mu", profile, "Technology", evidence, metrics,
            summary, new[] { (hit, why) }, "Low correlation (0.21) to your portfolio.", news,
            quote, recs, earnings, returnHistory, signalRecords,
            "Micron designs memory chips. Details at https://example.com/about for investors.",
            liveMetrics, new DateOnly(2026, 9, 25));
    }

    [Fact]
    public void Serialized_payload_contains_no_properties_outside_the_whitelist()
    {
        var json = BriefPayloadBuilder.Serialize(FullPayload());
        using var doc = JsonDocument.Parse(json);
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(doc.RootElement, names);

        var offenders = names.Where(n => !Whitelist.Contains(n)).ToList();
        Assert.True(offenders.Count == 0,
            $"non-whitelisted properties in AI payload: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Payload_never_contains_urls()
    {
        var json = BriefPayloadBuilder.Serialize(FullPayload());
        Assert.DoesNotContain("example.com", json);
        Assert.DoesNotContain("http", json);
    }

    [Fact]
    public void Headlines_filter_to_company_mentions_and_cap_at_six_newest()
    {
        var payload = FullPayload();
        Assert.Equal(BriefPayloadBuilder.MaxHeadlines, payload.Headlines.Count);
        Assert.Equal("Micron ships new part, day 0", payload.Headlines[0].Title);   // newest first
    }

    [Fact]
    public void Headlines_not_mentioning_ticker_or_company_name_are_dropped()
    {
        var news = new List<NewsItem>
        {
            new() { PublishedAt = DateTimeOffset.Now, Source = "Wire",
                Headline = "Peer company posts wider loss", Url = "https://x.test/1" },
            new() { PublishedAt = DateTimeOffset.Now, Source = "Wire",
                Headline = "Micron beats on strong DRAM demand", Url = "https://x.test/2" },
            new() { PublishedAt = DateTimeOffset.Now, Source = "Wire",
                Headline = "Chips rally as (NASDAQ:MU) leads sector", Url = "https://x.test/3" },
            // lowercase "mu" is a word, not the ticker — must not match
            new() { PublishedAt = DateTimeOffset.Now, Source = "Wire",
                Headline = "Greek letter mu adopted by standards body", Url = "https://x.test/4" },
        };
        var profile = new CompanyProfile { Ticker = "MU", Name = "Micron Technology Inc" };

        var payload = BriefPayloadBuilder.Build("MU", profile, "Technology",
            new List<EvidenceItem>(), null, null,
            Array.Empty<(SignalHit, SignalWhy)>(), null, news);

        Assert.Equal(2, payload.Headlines.Count);
        Assert.All(payload.Headlines, h => Assert.Contains(h.Title,
            new[] { "Micron beats on strong DRAM demand", "Chips rally as (NASDAQ:MU) leads sector" }));
    }

    [Fact]
    public void Fundamentals_carry_precomputed_sector_median_multiples()
    {
        var payload = FullPayload();
        var pe = payload.Fundamentals.Single(f => f.Name == "P/E (TTM)");
        Assert.Equal(0.55m, pe.XSectorMedian);   // 12 / 22, rounded to 2 decimals
        var growth = payload.Fundamentals.Single(f => f.Name == "Revenue growth YoY %");
        Assert.Equal(2.5m, growth.XSectorMedian);   // 20 / 8
        var beta = payload.Fundamentals.Single(f => f.Name == "Beta");
        Assert.Null(beta.XSectorMedian);   // no sector median, no multiple
    }

    [Fact]
    public void Price_block_precomputes_percent_off_52_week_high()
    {
        var payload = FullPayload();
        Assert.NotNull(payload.Price);
        Assert.Equal(95m, payload.Price!.Current);
        Assert.Equal(120m, payload.Price.Wk52High);
        Assert.Equal(20.8m, payload.Price.PctOff52wHigh);   // (120 - 95) / 120
    }

    [Fact]
    public void Sector_health_block_carries_quadrant_and_the_numbers_behind_it()
    {
        var payload = FullPayload();
        Assert.NotNull(payload.SectorHealth);
        Assert.Equal("HealthyRally", payload.SectorHealth!.Quadrant);
        Assert.Equal(60m, payload.SectorHealth.BreadthAboveSma50Pct);
        Assert.Equal(2m, payload.SectorHealth.MedianReturn20dPct);
    }

    [Fact]
    public void Analyst_recommendations_keep_the_two_newest_periods_with_precomputed_totals()
    {
        var payload = FullPayload();
        Assert.Equal(2, payload.AnalystRecommendations!.Count);
        Assert.Equal("2026-07-01", payload.AnalystRecommendations[0].Period);
        Assert.Equal("2026-06-01", payload.AnalystRecommendations[1].Period);
        // models total these anyway ("22 of 24 analysts"); the sums must be
        // literals in the data or the checker flags the section
        Assert.Equal(17, payload.AnalystRecommendations[0].BuyTotal);   // 6 + 11
        Assert.Equal(25, payload.AnalystRecommendations[0].Total);      // 6+11+7+1+0
    }

    [Fact]
    public void Earnings_record_precomputes_beats_and_misses()
    {
        var payload = FullPayload();
        Assert.NotNull(payload.EarningsRecord);
        Assert.Equal(4, payload.EarningsRecord!.Quarters);   // capped window
        Assert.Equal(4, payload.EarningsRecord.Beats);       // all surprises positive
        Assert.Equal(0, payload.EarningsRecord.Misses);
    }

    [Fact]
    public void Earnings_history_keeps_the_four_newest_quarters()
    {
        var payload = FullPayload();
        Assert.Equal(BriefPayloadBuilder.MaxEarningsQuarters, payload.EarningsHistory.Count);
        Assert.Equal("2026-06-30", payload.EarningsHistory[0].Period);
    }

    [Fact]
    public void Only_the_latest_firing_per_signal_kind_survives()
    {
        var newer = new SignalHit
        {
            Id = 2, Ticker = "MU", SignalKind = "CheapGrowth",
            FiredAt = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero), InputsJson = "{}",
        };
        var older = new SignalHit
        {
            Id = 1, Ticker = "MU", SignalKind = "CheapGrowth",
            FiredAt = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero), InputsJson = "{}",
        };
        var why = new SignalWhy("cheap grower", "Technology",
            new List<SignalConditionResult>(), 70);

        var payload = BriefPayloadBuilder.Build("MU", null, "Technology",
            new List<EvidenceItem>(), null, null,
            new[] { (older, why), (newer, why) }, null, new List<NewsItem>());

        var signal = Assert.Single(payload.Signals);
        Assert.Equal("2026-08-04", signal.FiredAt);
    }

    [Fact]
    public void Headline_summaries_are_truncated_and_stripped_of_urls()
    {
        var longSummary = new string('a', 400) + " https://leak.test/path tail";
        var news = new List<NewsItem>
        {
            new() { PublishedAt = DateTimeOffset.Now, Source = "Wire",
                Headline = "MU launch details", Url = "https://x.test/1", Summary = longSummary },
        };
        var payload = BriefPayloadBuilder.Build("MU", null, null,
            new List<EvidenceItem>(), null, null,
            Array.Empty<(SignalHit, SignalWhy)>(), null, news);

        var headline = Assert.Single(payload.Headlines);
        Assert.NotNull(headline.Summary);
        Assert.True(headline.Summary!.Length <= BriefPayloadBuilder.MaxHeadlineSummaryChars + 1);
        Assert.DoesNotContain("http", headline.Summary);
    }

    [Fact]
    public void Description_is_url_stripped_and_next_earnings_date_flows_through()
    {
        var payload = FullPayload();
        Assert.NotNull(payload.Profile?.Description);
        Assert.Contains("Micron designs memory chips.", payload.Profile!.Description);
        Assert.DoesNotContain("http", payload.Profile.Description);
        Assert.Equal("2026-09-25", payload.NextEarningsDate);
    }

    [Fact]
    public void Leverage_and_dividend_rows_come_from_the_live_metrics()
    {
        var payload = FullPayload();
        Assert.Equal(2.14m, payload.Fundamentals!.Single(f => f.Name == "Dividend yield %").Value);
        Assert.Equal(1.46m, payload.Fundamentals.Single(f => f.Name == "Debt/equity").Value);
        Assert.Equal(0.92m, payload.Fundamentals.Single(f => f.Name == "Current ratio").Value);
    }

    [Fact]
    public void Numbers_are_rounded_to_presentation_precision()
    {
        var profile = new CompanyProfile
        {
            Ticker = "VST", Name = "Vistra Corp", MarketCapMillions = 49737.783718593855m,
        };
        var metrics = new TickerMetricsRow { Ticker = "VST", Pe = 22.2968m };
        var earnings = new List<EarningsQuarter>
        {
            new() { Period = "2025-12-31", Estimate = 1.23456m, Actual = 0.2814m,
                SurprisePercent = -77.2046m },
        };
        var payload = BriefPayloadBuilder.Build("VST", profile, "Utilities",
            new List<EvidenceItem>(), metrics, null,
            Array.Empty<(SignalHit, SignalWhy)>(), null, new List<NewsItem>(),
            earnings: earnings);

        Assert.Equal(49738m, payload.Profile!.MarketCapMillions);
        Assert.Equal(22.3m, payload.Fundamentals!.Single(f => f.Name == "P/E (TTM)").Value);
        var q = payload.EarningsHistory!.Single();
        Assert.Equal(1.23m, q.EpsEstimate);
        Assert.Equal(-77.2m, q.SurprisePercent);
    }

    [Fact]
    public void Empty_blocks_are_omitted_from_the_serialized_payload()
    {
        // a small model told about an empty list writes filler about the
        // absence — absent blocks must not serialize at all
        var payload = BriefPayloadBuilder.Build("XYZ", null, null,
            new List<EvidenceItem>(), null, null,
            Array.Empty<(SignalHit, SignalWhy)>(), null, new List<NewsItem>());
        var json = BriefPayloadBuilder.Serialize(payload);

        Assert.Contains("\"ticker\"", json);
        foreach (var name in new[]
        {
            "evidence", "fundamentals", "signals", "headlines",
            "analystRecommendations", "earningsHistory", "earningsRecord",
            "signalTrackRecord", "profile", "price", "sectorHealth",
            "twoMonthReturnHistory",
        })
            Assert.DoesNotContain($"\"{name}\"", json);
    }

    [Fact]
    public void Return_history_and_fired_signal_records_flow_into_the_payload()
    {
        var payload = FullPayload();
        Assert.NotNull(payload.TwoMonthReturnHistory);
        Assert.Equal(34.1m, payload.TwoMonthReturnHistory!.PctReturnedAtLeast10);
        // only kinds with matured 60d outcomes make the cut, at the 60d horizon
        var record = Assert.Single(payload.SignalTrackRecord);
        Assert.Equal("CheapGrowth", record.Kind);
        Assert.Equal(24, record.Matured60d);
        Assert.Equal(58.3m, record.BeatRate60dPct);
    }

    [Fact]
    public void Ticker_normalizes_and_evidence_flows_through_verbatim()
    {
        var payload = FullPayload();
        Assert.Equal("MU", payload.Ticker);
        var e = Assert.Single(payload.Evidence);
        Assert.Equal("Pass", e.Status);
        Assert.Equal("z̄ = 1.2, 80th percentile", e.Observed);
    }

    private static void Collect(JsonElement element, HashSet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    names.Add(p.Name);
                    Collect(p.Value, names);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, names);
                break;
        }
    }
}
