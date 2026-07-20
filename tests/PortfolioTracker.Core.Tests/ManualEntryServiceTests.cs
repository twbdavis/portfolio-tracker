using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Core.Tests;

public class ManualEntryServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-test-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly ManualEntryService _svc;
    private readonly Account _account;

    public ManualEntryServiceTests()
    {
        _db = new Database(_dbPath);
        _svc = new ManualEntryService(_db);
        _account = TestData.Account(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private Rebuilder NewRebuilder() => new(new AccountRepository(_db), new PositionRepository(_db),
        new TransactionRepository(_db), new LotRepository(_db), new SaleRepository(_db),
        new LotConsumptionRepository(_db), new WashSaleAdjustmentRepository(_db));

    [Fact]
    public void AddLot_then_UpdateLot_then_DeleteLot_rebuilds_each_time()
    {
        var positions = new PositionRepository(_db);
        var lots = new LotRepository(_db);
        var txs = new TransactionRepository(_db);

        _svc.AddLot(_account.Id, "MU", 10m, 1000m, new DateOnly(2026, 1, 5), openingBalance: false);
        var pos = positions.GetByAccountAndTicker(_account.Id, "MU")!;
        Assert.Equal(10m, lots.GetByPosition(pos.Id, openOnly: true).Sum(l => l.RemainingShares));

        var txId = txs.GetByAccountAndTicker(_account.Id, "MU").Single().Id;
        _svc.UpdateLot(txId, 12m, 1200m, new DateOnly(2026, 1, 6));
        Assert.Equal(12m, lots.GetByPosition(pos.Id, openOnly: true).Sum(l => l.RemainingShares));

        _svc.DeleteLot(txId);
        Assert.Empty(lots.GetByPosition(pos.Id, openOnly: true));
    }

    [Fact]
    public void DeletePosition_removes_position_transactions_lots_sales_and_price()
    {
        var positions = new PositionRepository(_db);
        var lots = new LotRepository(_db);
        var sales = new SaleRepository(_db);
        var txs = new TransactionRepository(_db);
        var cache = new PriceCacheRepository(_db);

        _svc.AddLot(_account.Id, "MU", 10m, 1000m, new DateOnly(2026, 1, 5), openingBalance: false);
        var pos = positions.GetByAccountAndTicker(_account.Id, "MU")!;
        // a sell so SaleRecord rows exist too
        txs.Insert(new Transaction { AccountId = _account.Id, Ticker = "MU", TradeDate = new DateOnly(2026, 2, 5), Type = TxType.Sell,
            Shares = 4m, Amount = 500m, Source = LotSource.Manual });
        NewRebuilder().RebuildTicker(_account.Id, "MU");
        Assert.Single(sales.GetByPosition(pos.Id));
        cache.Upsert(new PriceQuote("MU", 123m, DateTimeOffset.UtcNow));

        _svc.DeletePosition(pos.Id);

        Assert.Null(positions.GetByAccountAndTicker(_account.Id, "MU"));
        Assert.Empty(txs.GetByAccountAndTicker(_account.Id, "MU"));
        Assert.Empty(lots.GetByPosition(pos.Id, openOnly: false));
        Assert.Empty(sales.GetByPosition(pos.Id));
        Assert.False(cache.GetAll().ContainsKey("MU"));
    }

    [Fact]
    public void DeletePosition_keeps_price_and_target_while_another_account_holds_the_ticker()
    {
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        _svc.AddLot(_account.Id, "VOO", 1m, 505.31m, new DateOnly(2026, 5, 20), openingBalance: false);
        _svc.AddLot(roth.Id, "VOO", 0.1m, 51m, new DateOnly(2026, 6, 12), openingBalance: false);
        var cache = new PriceCacheRepository(_db);
        cache.Upsert(new PriceQuote("VOO", 510m, DateTimeOffset.UtcNow));
        var targets = new TickerTargetRepository(_db);
        targets.Upsert(new TickerTarget { Ticker = "VOO", TargetPrice = 550m });

        var positions = new PositionRepository(_db);
        _svc.DeletePosition(positions.GetByAccountAndTicker(_account.Id, "VOO")!.Id);

        Assert.Single(positions.GetAllByTicker("VOO"));    // Roth position survives
        Assert.True(cache.GetAll().ContainsKey("VOO"));
        Assert.NotNull(targets.GetByTicker("VOO"));

        _svc.DeletePosition(positions.GetByAccountAndTicker(roth.Id, "VOO")!.Id);
        Assert.False(cache.GetAll().ContainsKey("VOO"));   // last holder gone: cleanup
        Assert.Null(targets.GetByTicker("VOO"));
    }

    [Fact]
    public void DeletePosition_removes_imported_transactions_and_their_dedupe_keys()
    {
        var positions = new PositionRepository(_db);
        var txs = new TransactionRepository(_db);
        txs.Insert(new Transaction { AccountId = _account.Id, Ticker = "RCAT", TradeDate = new DateOnly(2026, 3, 1), Type = TxType.Buy,
            Shares = 5m, Amount = 50m, Source = LotSource.ChaseImport, ImportBatchId = null, DedupeKey = "k-rcat" });
        NewRebuilder().RebuildTicker(_account.Id, "RCAT");

        _svc.DeletePosition(positions.GetByAccountAndTicker(_account.Id, "RCAT")!.Id);

        Assert.Null(positions.GetByAccountAndTicker(_account.Id, "RCAT"));
        Assert.DoesNotContain("k-rcat", txs.GetAllDedupeKeys()); // re-import can restore the rows
    }

    [Fact]
    public void DeletePosition_unknown_id_throws()
    {
        Assert.Throws<InvalidOperationException>(() => _svc.DeletePosition(999));
    }

    [Fact]
    public void DeleteAccount_cascades_positions_transactions_cash_flows_and_batches()
    {
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        _svc.AddLot(_account.Id, "VOO", 1m, 505.31m, new DateOnly(2026, 5, 20), openingBalance: false);
        _svc.AddLot(roth.Id, "VOO", 0.1m, 51m, new DateOnly(2026, 6, 12), openingBalance: false);
        var cash = new CashFlowRepository(_db);
        cash.Insert(new CashFlow { AccountId = roth.Id, TradeDate = new(2026, 6, 12), Type = CashFlowType.Deposit, Amount = 51m });
        var batches = new ImportBatchRepository(_db);
        batches.Insert(new ImportBatch { AccountId = roth.Id, ImportedAt = DateTimeOffset.UtcNow, FileName = "roth.csv", RowsImported = 1 });

        _svc.DeleteAccount(roth.Id);

        var accounts = new AccountRepository(_db);
        Assert.Null(accounts.GetById(roth.Id));
        var positions = new PositionRepository(_db);
        Assert.Null(positions.GetByAccountAndTicker(roth.Id, "VOO"));
        Assert.Empty(new TransactionRepository(_db).GetByAccountAndTicker(roth.Id, "VOO"));
        Assert.Equal(0m, cash.GetTotals(roth.Id).Deposits);
        Assert.DoesNotContain(batches.GetAll(), b => b.AccountId == roth.Id);

        // the other account's identical ticker is untouched
        var survivor = positions.GetByAccountAndTicker(_account.Id, "VOO")!;
        Assert.Equal(1m, new LotRepository(_db).GetByPosition(survivor.Id, openOnly: true).Sum(l => l.RemainingShares));
    }

    [Fact]
    public void DeleteAccount_with_no_data_just_removes_the_account_row()
    {
        var empty = TestData.Account(_db, "...0000", "Accidental duplicate");
        _svc.DeleteAccount(empty.Id);
        Assert.Null(new AccountRepository(_db).GetById(empty.Id));
        Assert.Throws<InvalidOperationException>(() => _svc.DeleteAccount(empty.Id)); // already gone
    }
}
