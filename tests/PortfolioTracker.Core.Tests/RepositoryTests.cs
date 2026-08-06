using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tests;

public class RepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-test-{Guid.NewGuid():N}.db");
    private readonly Database _db;

    public RepositoryTests() => _db = new Database(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Position_GetOrCreate_roundtrips_and_is_idempotent()
    {
        var account = TestData.Account(_db);
        var repo = new PositionRepository(_db);
        var p1 = repo.GetOrCreate(account.Id, "MU", "595112103", "MICRON TECHNOLOGY INC");
        var p2 = repo.GetOrCreate(account.Id, "mu", null, null); // NOCASE: same position
        Assert.Equal(p1.Id, p2.Id);
        Assert.Equal(account.Id, p1.AccountId);
    }

    [Fact]
    public void Same_ticker_in_two_accounts_is_two_positions()
    {
        var taxable = TestData.Account(_db);
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        var repo = new PositionRepository(_db);
        var p1 = repo.GetOrCreate(taxable.Id, "VOO", null, null);
        var p2 = repo.GetOrCreate(roth.Id, "VOO", null, null);
        Assert.NotEqual(p1.Id, p2.Id);
        Assert.Equal(2, repo.GetAllByTicker("VOO").Count);
        Assert.Single(repo.GetByAccount(taxable.Id));
    }

    [Fact]
    public void TickerTarget_upserts_by_ticker_and_stamps_alert()
    {
        var repo = new TickerTargetRepository(_db);
        repo.Upsert(new TickerTarget { Ticker = "MU", TargetPrice = 195.50m, TargetDirection = TargetDirection.AtOrBelow, AlertEnabled = true });
        repo.Upsert(new TickerTarget { Ticker = "MU", TargetPrice = 200m, TargetDirection = TargetDirection.AtOrAbove, AlertEnabled = true });
        var got = Assert.Single(repo.GetAll());   // one target per ticker
        Assert.Equal(200m, got.TargetPrice);
        repo.StampAlert("MU", DateTimeOffset.UtcNow);
        Assert.NotNull(repo.GetByTicker("MU")!.AlertTriggeredAt);
        repo.StampAlert("MU", null);              // re-arm
        Assert.Null(repo.GetByTicker("MU")!.AlertTriggeredAt);
    }

    [Fact]
    public void Account_insert_and_lookup_by_number()
    {
        var repo = new AccountRepository(_db);
        var a = TestData.Account(_db, "...5678", "Roth IRA", TaxTreatment.RothIRA);
        var got = repo.GetByNumber("...5678")!;
        Assert.Equal(a.Id, got.Id);
        Assert.Equal(TaxTreatment.RothIRA, got.TaxTreatment);
        Assert.True(got.IsRetirement);
        got.DisplayName = "My Roth";
        repo.Update(got);
        Assert.Equal("My Roth", repo.GetById(got.Id)!.DisplayName);
    }

    [Fact]
    public void Transaction_insert_query_deleteBatch()
    {
        var account = TestData.Account(_db);
        var batches = new ImportBatchRepository(_db);
        var txRepo = new TransactionRepository(_db);
        var batchId = batches.Insert(new ImportBatch { AccountId = account.Id, ImportedAt = DateTimeOffset.UtcNow, FileName = "a.csv", RowsImported = 1 });
        txRepo.Insert(new Transaction { AccountId = account.Id, Ticker = "MU", TradeDate = new DateOnly(2026, 7, 2), Type = TxType.Buy,
            Shares = 0.06235m, Amount = 61.37m, Source = LotSource.ChaseImport, ImportBatchId = batchId, DedupeKey = "k1" });
        txRepo.Insert(new Transaction { AccountId = account.Id, Ticker = "DRAM", TradeDate = new DateOnly(2026, 7, 6), Type = TxType.Buy,
            Shares = 1m, Amount = 65.05m, Source = LotSource.Manual });

        Assert.Equal(0.06235m, txRepo.GetByAccountAndTicker(account.Id, "MU").Single().Shares);
        Assert.Equal(new[] { "MU" }, txRepo.GetTickersForBatch(batchId));
        Assert.Contains("k1", txRepo.GetAllDedupeKeys());

        txRepo.DeleteBatch(batchId);
        Assert.Empty(txRepo.GetByAccountAndTicker(account.Id, "MU"));
        Assert.Single(txRepo.GetByAccountAndTicker(account.Id, "DRAM")); // manual row untouched
    }

    [Fact]
    public void Lot_ReplaceForPosition_replaces_and_filters_open()
    {
        var account = TestData.Account(_db);
        var positions = new PositionRepository(_db);
        var lots = new LotRepository(_db);
        var pos = positions.GetOrCreate(account.Id, "RCAT", null, null);
        lots.ReplaceForPosition(pos.Id, new List<Lot> {
            new() { AccountId = account.Id, PositionId = pos.Id, Shares = 10m, TotalCost = 100m, RemainingShares = 0m, RemainingCost = 0m,
                    TradeDate = new DateOnly(2026, 1, 5), Source = LotSource.ChaseImport },
            new() { AccountId = account.Id, PositionId = pos.Id, Shares = 5m, TotalCost = 50m, RemainingShares = 5m, RemainingCost = 50m,
                    TradeDate = new DateOnly(2026, 2, 5), Source = LotSource.ChaseImport },
        });
        Assert.Equal(2, lots.GetByPosition(pos.Id, openOnly: false).Count);
        Assert.Single(lots.GetByPosition(pos.Id, openOnly: true));
        lots.ReplaceForPosition(pos.Id, new List<Lot>());
        Assert.Empty(lots.GetByPosition(pos.Id, openOnly: false));
    }

    [Fact]
    public void CashFlow_totals_scope_by_account()
    {
        var taxable = TestData.Account(_db);
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        var cash = new CashFlowRepository(_db);
        cash.Insert(new CashFlow { AccountId = taxable.Id, TradeDate = new(2026, 1, 5), Type = CashFlowType.Deposit, Amount = 500m });
        cash.Insert(new CashFlow { AccountId = roth.Id, TradeDate = new(2026, 1, 6), Type = CashFlowType.Deposit, Amount = 51m });
        cash.Insert(new CashFlow { AccountId = roth.Id, TradeDate = new(2026, 2, 6), Type = CashFlowType.Dividend, Ticker = "VOO", Amount = 1.25m });

        Assert.Equal(551m, cash.GetTotals().Deposits);
        Assert.Equal(500m, cash.GetTotals(taxable.Id).Deposits);
        Assert.Equal(1.25m, cash.GetTotals(roth.Id).Dividends);
    }

    [Fact]
    public void PriceCache_upsert_overwrites()
    {
        var cache = new PriceCacheRepository(_db);
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-5);
        cache.Upsert(new PriceQuote("NVDA", 190.10m, t1));
        cache.Upsert(new PriceQuote("NVDA", 195.00m, t1.AddMinutes(5)));
        var all = cache.GetAll();
        Assert.Equal(195.00m, all["NVDA"].Price);
        Assert.Single(all);
    }

    [Fact]
    public void PriceCache_prev_close_round_trips()
    {
        var cache = new PriceCacheRepository(_db);
        cache.Upsert(new PriceQuote("NVDA", 195.00m, DateTimeOffset.UtcNow, PrevClose: 196.93m));
        Assert.Equal(196.93m, cache.GetAll()["NVDA"].PrevClose);
    }

    [Fact]
    public void PriceCache_observed_at_round_trips_and_null_is_fine()
    {
        var cache = new PriceCacheRepository(_db);
        var observed = new DateTimeOffset(2026, 7, 29, 13, 13, 45, TimeSpan.FromHours(-5));
        cache.Upsert(new PriceQuote("NVDA", 193.16m, DateTimeOffset.UtcNow, ObservedAt: observed));
        cache.Upsert(new PriceQuote("VOO", 678.20m, DateTimeOffset.UtcNow));   // pre-field rows stay null
        var all = cache.GetAll();
        Assert.Equal(observed, all["NVDA"].ObservedAt);
        Assert.Null(all["VOO"].ObservedAt);
    }
}
