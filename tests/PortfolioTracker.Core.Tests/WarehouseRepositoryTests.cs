using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tests;

public class WarehouseRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-test-{Guid.NewGuid():N}.db");
    private readonly Database _db;

    public WarehouseRepositoryTests() => _db = new Database(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Universe_crud_and_members_roundtrip()
    {
        var repo = new UniverseRepository(_db);
        var id = repo.Insert("Semis", isSeeded: true);
        repo.AddMembers(id, new[] { "NVDA", "amd", "MU", "NVDA" });   // dupe + case both collapse
        Assert.Equal(new[] { "AMD", "MU", "NVDA" }, repo.GetMembers(id));
        Assert.True(repo.AnySeeded());
        Assert.Equal(3, repo.GetMemberCounts()[id]);

        repo.RemoveMember(id, "MU");
        Assert.Equal(new[] { "AMD", "NVDA" }, repo.GetMembers(id));

        repo.Rename(id, "Semiconductors");
        Assert.Equal("Semiconductors", repo.Get(id)!.Name);

        repo.Delete(id);
        Assert.Empty(repo.GetAll());
        Assert.Empty(repo.GetAllTickers());
    }

    [Fact]
    public void GetAllTickers_is_distinct_across_universes()
    {
        var repo = new UniverseRepository(_db);
        var a = repo.Insert("A", false);
        var b = repo.Insert("B", false);
        repo.AddMembers(a, new[] { "NVDA", "MU" });
        repo.AddMembers(b, new[] { "NVDA", "VOO" });
        Assert.Equal(new[] { "MU", "NVDA", "VOO" }, repo.GetAllTickers());
    }

    [Fact]
    public void Classification_crawl_never_overwrites_user_override()
    {
        var repo = new TickerClassificationRepository(_db);
        repo.UpsertFromCrawl("NVDA", "Technology", "Semiconductors");
        Assert.Equal("Technology", repo.Get("NVDA")!.Sector);

        repo.SetUserOverride("NVDA", "Custom Sector", null);
        repo.UpsertFromCrawl("NVDA", "Technology", "Semiconductors");
        var c = repo.Get("NVDA")!;
        Assert.Equal("Custom Sector", c.Sector);
        Assert.Equal(ClassificationSource.User, c.Source);

        repo.UpsertFromCrawl("MU", "Technology", "Memory");   // fresh crawl rows still insert
        Assert.Equal(2, repo.GetAll().Count);
    }

    [Fact]
    public void Metrics_upsert_replaces_snapshot()
    {
        var repo = new TickerMetricsRepository(_db);
        repo.Upsert(new TickerMetricsRow { Ticker = "NVDA", AsOf = DateTimeOffset.Now, Pe = 40m, Roe = 0.9m });
        repo.Upsert(new TickerMetricsRow { Ticker = "NVDA", AsOf = DateTimeOffset.Now, Pe = 42m });
        var m = repo.Get("NVDA")!;
        Assert.Equal(42m, m.Pe);
        Assert.Null(m.Roe);   // full-row replace: a vanished metric goes null, not stale
        Assert.Single(repo.GetAll());
    }

    [Fact]
    public void Recommendation_snapshots_keyed_by_month()
    {
        var repo = new RecommendationSnapshotRepository(_db);
        repo.UpsertMany("NVDA", new[]
        {
            new RecommendationSnapshotRow { Month = "2026-05-01", StrongBuy = 10, Buy = 5, Hold = 3 },
            new RecommendationSnapshotRow { Month = "2026-06-01", StrongBuy = 12, Buy = 5, Hold = 2 },
        });
        repo.UpsertMany("NVDA", new[]
        {
            new RecommendationSnapshotRow { Month = "2026-06-01", StrongBuy = 13, Buy = 5, Hold = 1 },
        });
        var rows = repo.GetForTicker("NVDA", 6);
        Assert.Equal(2, rows.Count);
        Assert.Equal("2026-06-01", rows[0].Month);   // newest first
        Assert.Equal(13, rows[0].StrongBuy);
        Assert.Equal(18m / 19m, rows[0].BuyRatio!.Value, 5);   // (13+5)/(13+5+1)
    }

    [Fact]
    public void BuyRatio_null_when_no_analysts()
    {
        Assert.Null(new RecommendationSnapshotRow().BuyRatio);
    }

    [Fact]
    public void DailyClose_stooq_wins_over_live_but_live_refreshes_live()
    {
        var repo = new DailyCloseRepository(_db);
        var date = new DateOnly(2026, 7, 17);

        // live approximation first, refreshed by a later live quote
        repo.UpsertMany("NVDA", new[] { new DailyCloseRow { Date = date, Close = 100m, Source = CloseSource.LiveSnapshot } });
        repo.UpsertMany("NVDA", new[] { new DailyCloseRow { Date = date, Close = 101m, Source = CloseSource.LiveSnapshot } });
        Assert.Equal(101m, repo.GetForTicker("NVDA").Single().Close);

        // official Stooq close replaces the approximation
        repo.UpsertMany("NVDA", new[] { new DailyCloseRow { Date = date, Close = 102m, Source = CloseSource.Stooq } });
        var row = repo.GetForTicker("NVDA").Single();
        Assert.Equal(102m, row.Close);
        Assert.Equal(CloseSource.Stooq, row.Source);

        // a live snapshot never overwrites an official close
        repo.UpsertMany("NVDA", new[] { new DailyCloseRow { Date = date, Close = 999m, Source = CloseSource.LiveSnapshot } });
        Assert.Equal(102m, repo.GetForTicker("NVDA").Single().Close);
    }

    [Fact]
    public void DailyClose_queries_by_range_and_latest()
    {
        var repo = new DailyCloseRepository(_db);
        repo.UpsertMany("NVDA", new[]
        {
            new DailyCloseRow { Date = new DateOnly(2026, 7, 15), Close = 100m, Source = CloseSource.Stooq },
            new DailyCloseRow { Date = new DateOnly(2026, 7, 16), Close = 101m, Source = CloseSource.Stooq },
            new DailyCloseRow { Date = new DateOnly(2026, 7, 17), Close = 102m, Source = CloseSource.Stooq },
        });
        repo.UpsertMany("MU", new[]
        {
            new DailyCloseRow { Date = new DateOnly(2026, 7, 17), Close = 50m, Source = CloseSource.LiveSnapshot },
        });

        Assert.Equal(new DateOnly(2026, 7, 17), repo.GetLatestDate("NVDA"));
        Assert.Null(repo.GetLatestDate("ZZZZ"));
        Assert.Equal(3, repo.CountForTicker("NVDA"));
        Assert.Equal(2, repo.GetForTicker("NVDA", new DateOnly(2026, 7, 16)).Count);

        var all = repo.GetAllByTicker(new DateOnly(2026, 7, 1));
        Assert.Equal(2, all.Count);
        Assert.Equal(100m, all["NVDA"][0].Close);   // oldest first
    }

    [Fact]
    public void SignalHit_roundtrip_dismiss_and_latest()
    {
        var repo = new SignalHitRepository(_db);
        var id1 = repo.Insert(new SignalHit
        {
            Ticker = "NVDA", SignalKind = "CheapGrowth",
            FiredAt = DateTimeOffset.Now.AddDays(-20), InputsJson = "{\"pe\":-1.2}",
        });
        var id2 = repo.Insert(new SignalHit
        {
            Ticker = "NVDA", SignalKind = "CheapGrowth",
            FiredAt = DateTimeOffset.Now, InputsJson = "{\"pe\":-1.4}",
        });

        Assert.Equal(2, repo.GetAll().Count);
        Assert.Equal(id2, repo.GetLatest("NVDA", "CheapGrowth")!.Id);
        Assert.Null(repo.GetLatest("NVDA", "QualityOnSale"));

        repo.Dismiss(id2, DateTimeOffset.Now);
        Assert.Single(repo.GetAll());                       // dismissed hidden by default
        Assert.Equal(2, repo.GetAll(includeDismissed: true).Count);
        Assert.Equal(id2, repo.GetLatest("NVDA", "CheapGrowth")!.Id);   // dedupe still sees it
        Assert.Single(repo.GetForTicker("NVDA"));
        Assert.Equal(id1, repo.GetForTicker("NVDA").Single().Id);
    }

    [Fact]
    public void AppState_get_set_roundtrip()
    {
        var repo = new AppStateRepository(_db);
        Assert.Null(repo.Get("crawler.lastRun"));
        repo.Set("crawler.lastRun", "2026-07-20");
        repo.Set("crawler.lastRun", "2026-07-21");
        Assert.Equal("2026-07-21", repo.Get("crawler.lastRun"));
    }
}
