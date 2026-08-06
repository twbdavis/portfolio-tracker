using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;
using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Tests;

public class ResearchQueueRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-test-{Guid.NewGuid():N}.db");
    private readonly Database _db;

    public ResearchQueueRepositoryTests() => _db = new Database(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Discovery_first_source_wins_and_ignored_never_resurfaces()
    {
        var repo = new DiscoveredTickerRepository(_db);
        Assert.True(repo.InsertIfNew("smci", "MU", DateTimeOffset.Now));
        Assert.False(repo.InsertIfNew("SMCI", "NVDA", DateTimeOffset.Now));   // re-discovery is a no-op
        Assert.Equal("MU", repo.Get("SMCI")!.SourceTicker);

        repo.SetStatus("SMCI", DiscoveryStatus.Ignored);
        Assert.False(repo.InsertIfNew("SMCI", "AMD", DateTimeOffset.Now));
        Assert.Equal(DiscoveryStatus.Ignored, repo.Get("SMCI")!.Status);
        Assert.Empty(repo.GetAll(DiscoveryStatus.New));
    }

    [Fact]
    public void Snooze_prunes_expired_rows_on_read()
    {
        var repo = new QueueSnoozeRepository(_db);
        var now = DateTimeOffset.Now;
        repo.Snooze("MU", now.AddDays(30));
        repo.Snooze("NVDA", now.AddDays(-1));   // already lapsed

        var active = repo.GetActive(now);
        Assert.True(active.ContainsKey("MU"));
        Assert.False(active.ContainsKey("NVDA"));

        repo.Unsnooze("MU");
        Assert.Empty(repo.GetActive(now));
    }

    [Fact]
    public void PortfolioFit_upserts_and_preserves_null_correlation()
    {
        var repo = new PortfolioFitRepository(_db);
        var asOf = DateTimeOffset.Now;
        repo.UpsertMany(new[]
        {
            new PortfolioFitRow { Ticker = "MU", AsOf = asOf, Correlation = 0.42m, Observations = 88 },
            new PortfolioFitRow { Ticker = "NEW", AsOf = asOf, Correlation = null, Observations = 12 },
        });
        repo.UpsertMany(new[]
        {
            new PortfolioFitRow { Ticker = "MU", AsOf = asOf, Correlation = 0.5m, Observations = 90 },
        });

        var all = repo.GetAll();
        Assert.Equal(0.5m, all["MU"].Correlation);
        Assert.Equal(90, all["MU"].Observations);
        Assert.Null(all["NEW"].Correlation);
        Assert.Equal(12, all["NEW"].Observations);
    }

    [Fact]
    public void SignalOutcome_upsert_fills_horizons_incrementally()
    {
        var hits = new SignalHitRepository(_db);
        var id = hits.Insert(new SignalHit
        {
            Ticker = "MU", SignalKind = SignalKinds.CheapGrowth,
            FiredAt = DateTimeOffset.Now, InputsJson = "{}",
        });

        var repo = new SignalOutcomeRepository(_db);
        var row = new SignalOutcomeRow
        {
            SignalHitId = id, Ticker = "MU", SignalKind = SignalKinds.CheapGrowth,
            Sector = "Technology", FireDate = new DateOnly(2026, 5, 1),
            TickerCloseAtFire = 100m, SectorClosesJson = """{"MU":100}""",
        };
        repo.Upsert(row);

        row.Return30 = 5m;
        row.Excess30 = 2m;
        repo.Upsert(row);

        var stored = Assert.Single(repo.GetAll());
        Assert.Equal(5m, stored.Return30);
        Assert.Equal(2m, stored.Excess30);
        Assert.Null(stored.Return60);
        Assert.Equal(100m, stored.TickerCloseAtFire);
        Assert.Contains(id, repo.GetTrackedHitIds());
    }

    [Fact]
    public void Briefs_roundtrip_with_snapshot_only_on_request()
    {
        var repo = new ResearchBriefRepository(_db);
        var id = repo.Insert(new ResearchBriefRow
        {
            Ticker = "mu", CreatedAt = DateTimeOffset.Now, Model = "claude-sonnet-4-6",
            InputSnapshotJson = """{"ticker":"MU"}""", BriefMarkdown = "## What the company does",
            TokensUsed = 1234,
        });

        var list = repo.GetForTicker("MU");
        Assert.Equal("", Assert.Single(list).InputSnapshotJson);   // list view omits the blob

        var full = repo.Get(id)!;
        Assert.Equal("""{"ticker":"MU"}""", full.InputSnapshotJson);
        Assert.Equal(1234, full.TokensUsed);
        Assert.Equal(id, repo.GetLatestIdByTicker()["MU"]);

        // history view: newest first across tickers, heavy bodies omitted
        var laterId = repo.Insert(new ResearchBriefRow
        {
            Ticker = "AVGO", CreatedAt = DateTimeOffset.Now.AddMinutes(1), Model = "qwen3-27b",
            InputSnapshotJson = """{"ticker":"AVGO"}""", BriefMarkdown = "## Later brief",
            TokensUsed = 99, NeedsReview = true,
        });
        var all = repo.GetAllSummaries();
        Assert.Equal(new[] { laterId, id }, all.Select(b => b.Id));
        Assert.All(all, b => Assert.Equal("", b.BriefMarkdown));
        Assert.All(all, b => Assert.Equal("", b.InputSnapshotJson));
        Assert.True(all[0].NeedsReview);
    }

    [Fact]
    public void AiUsage_accumulates_per_month()
    {
        var repo = new AiUsageRepository(_db);
        repo.AddTokens("2026-08", 1000);
        repo.AddTokens("2026-08", 500);
        repo.AddTokens("2026-09", 10);
        Assert.Equal(1500, repo.GetTokensUsed("2026-08"));
        Assert.Equal(10, repo.GetTokensUsed("2026-09"));
        Assert.Equal(0, repo.GetTokensUsed("2026-07"));
        Assert.Equal("2026-08", AiUsageRepository.MonthKey(new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void QueueSettings_hot_reload_writes_defaults_then_honors_edits()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pt-queue-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "queue.json");
        try
        {
            var defaults = QueueSettings.Load(path);
            Assert.True(File.Exists(path));   // first load writes the editable file
            Assert.Equal(1.5m, defaults.WeightPortfolioFit);
            Assert.Equal(30, defaults.SnoozeDays);

            var edited = File.ReadAllText(path)
                .Replace("\"WeightQuality\": 1.0", "\"WeightQuality\": 2.5")
                .Replace("\"SnoozeDays\": 30", "\"SnoozeDays\": 7");
            File.WriteAllText(path, edited);

            var reloaded = QueueSettings.Load(path);   // no restart needed
            Assert.Equal(2.5m, reloaded.WeightQuality);
            Assert.Equal(7, reloaded.SnoozeDays);

            File.WriteAllText(path, "{ not json");
            var corrupted = QueueSettings.Load(path);   // corrupted: defaults, file preserved
            Assert.Equal(1.0m, corrupted.WeightQuality);
            Assert.Contains("not json", File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
