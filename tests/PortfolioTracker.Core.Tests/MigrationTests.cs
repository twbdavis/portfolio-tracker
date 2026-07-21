using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tests;

/// Forward-only automatic migration, tested against a database seeded with the
/// PRE-multi-account schema exactly as v0 created it.
public class MigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-mig-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    /// The v0 schema (targets on Position, no Account/TickerTarget, no AccountId columns).
    private void SeedV0Database(bool withData)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Position (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ticker TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Cusip TEXT NULL, Name TEXT NULL,
                TargetPrice TEXT NULL,
                TargetDirection INTEGER NOT NULL DEFAULT 0,
                AlertEnabled INTEGER NOT NULL DEFAULT 0,
                AlertTriggeredAt TEXT NULL,
                NeedsOpeningBalance INTEGER NOT NULL DEFAULT 0,
                Notes TEXT NULL);
            CREATE TABLE ImportBatch (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ImportedAt TEXT NOT NULL, FileName TEXT NOT NULL,
                RowsImported INTEGER NOT NULL, RowsSkipped INTEGER NOT NULL,
                RowsDeduped INTEGER NOT NULL);
            CREATE TABLE Tx (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ticker TEXT NOT NULL COLLATE NOCASE,
                Cusip TEXT NULL, TradeDate TEXT NOT NULL, Type INTEGER NOT NULL,
                Shares TEXT NOT NULL, Amount TEXT NOT NULL, Description TEXT NULL,
                Source INTEGER NOT NULL,
                ImportBatchId INTEGER NULL REFERENCES ImportBatch(Id),
                DedupeKey TEXT NULL);
            CREATE TABLE Lot (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PositionId INTEGER NOT NULL REFERENCES Position(Id),
                Shares TEXT NOT NULL, TotalCost TEXT NOT NULL,
                RemainingShares TEXT NOT NULL, RemainingCost TEXT NOT NULL,
                TradeDate TEXT NOT NULL, Source INTEGER NOT NULL,
                ImportBatchId INTEGER NULL, ParentLotId INTEGER NULL, SourceTxId INTEGER NULL);
            CREATE TABLE SaleRecord (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PositionId INTEGER NOT NULL REFERENCES Position(Id),
                TradeDate TEXT NOT NULL,
                SharesSold TEXT NOT NULL, Proceeds TEXT NOT NULL,
                CostBasisConsumed TEXT NOT NULL, RealizedGainLoss TEXT NOT NULL,
                UnmatchedShares TEXT NOT NULL DEFAULT '0',
                ImportBatchId INTEGER NULL);
            CREATE TABLE CashFlow (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TradeDate TEXT NOT NULL, Type INTEGER NOT NULL,
                Ticker TEXT NULL COLLATE NOCASE, Amount TEXT NOT NULL,
                Description TEXT NULL,
                ImportBatchId INTEGER NULL REFERENCES ImportBatch(Id),
                DedupeKey TEXT NULL);
            CREATE TABLE LotConsumption (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PositionId INTEGER NOT NULL, SaleRecordId INTEGER NOT NULL, LotId INTEGER NOT NULL,
                SharesConsumed TEXT NOT NULL, CostBasisConsumed TEXT NOT NULL, Proceeds TEXT NOT NULL,
                LotAcquisitionDate TEXT NOT NULL, SaleTradeDate TEXT NOT NULL,
                HoldingDays INTEGER NOT NULL, Term INTEGER NOT NULL, GainLoss TEXT NOT NULL);
            CREATE TABLE WashSaleAdjustment (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PositionId INTEGER NOT NULL, LotConsumptionId INTEGER NOT NULL,
                ReplacementLotId INTEGER NOT NULL,
                SharesMatched TEXT NOT NULL, DisallowedLoss TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        if (!withData) return;
        var seed = conn.CreateCommand();
        seed.CommandText = """
            INSERT INTO Position (Id, Ticker, Cusip, TargetPrice, TargetDirection, AlertEnabled, Notes)
            VALUES (1, 'MU', '595112103', '195.5', 1, 1, 'chips'),
                   (2, 'VOO', NULL, NULL, 0, 0, NULL);
            INSERT INTO ImportBatch (Id, ImportedAt, FileName, RowsImported, RowsSkipped, RowsDeduped)
            VALUES (7, '2026-06-01T12:00:00.0000000-04:00', 'old.csv', 2, 0, 0);
            INSERT INTO Tx (Ticker, TradeDate, Type, Shares, Amount, Source, ImportBatchId, DedupeKey)
            VALUES ('MU', '2026-01-05', 0, '10', '1000', 1, 7, '1/5/2026|Buy|MU|10|-1000'),
                   ('VOO', '2026-05-20', 0, '1', '505.31', 1, 7, '5/20/2026|Buy|VOO|1|-505.31');
            INSERT INTO Lot (PositionId, Shares, TotalCost, RemainingShares, RemainingCost, TradeDate, Source)
            VALUES (1, '10', '1000', '10', '1000', '2026-01-05', 1);
            INSERT INTO CashFlow (TradeDate, Type, Amount, DedupeKey)
            VALUES ('2026-01-15', 0, '500', '1/15/2026|BNK|||500|BANKLINK');
            """;
        seed.ExecuteNonQuery();
    }

    /// EARLY v0: a build predating CashFlow/LotConsumption/WashSaleAdjustment
    /// (the 2026-07-20 laptop schema). The migration must skip tables that
    /// don't exist — CreateSchema creates them in v1 form right afterwards.
    private void SeedEarlyV0Database()
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Position (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ticker TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Cusip TEXT NULL, Name TEXT NULL,
                TargetPrice TEXT NULL,
                TargetDirection INTEGER NOT NULL DEFAULT 0,
                AlertEnabled INTEGER NOT NULL DEFAULT 0,
                AlertTriggeredAt TEXT NULL,
                NeedsOpeningBalance INTEGER NOT NULL DEFAULT 0,
                Notes TEXT NULL);
            CREATE TABLE ImportBatch (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ImportedAt TEXT NOT NULL, FileName TEXT NOT NULL,
                RowsImported INTEGER NOT NULL, RowsSkipped INTEGER NOT NULL,
                RowsDeduped INTEGER NOT NULL);
            CREATE TABLE Tx (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ticker TEXT NOT NULL COLLATE NOCASE,
                Cusip TEXT NULL, TradeDate TEXT NOT NULL, Type INTEGER NOT NULL,
                Shares TEXT NOT NULL, Amount TEXT NOT NULL, Description TEXT NULL,
                Source INTEGER NOT NULL,
                ImportBatchId INTEGER NULL REFERENCES ImportBatch(Id),
                DedupeKey TEXT NULL);
            CREATE TABLE Lot (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PositionId INTEGER NOT NULL REFERENCES Position(Id),
                Shares TEXT NOT NULL, TotalCost TEXT NOT NULL,
                RemainingShares TEXT NOT NULL, RemainingCost TEXT NOT NULL,
                TradeDate TEXT NOT NULL, Source INTEGER NOT NULL,
                ImportBatchId INTEGER NULL, ParentLotId INTEGER NULL, SourceTxId INTEGER NULL);
            CREATE TABLE SaleRecord (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PositionId INTEGER NOT NULL REFERENCES Position(Id),
                TradeDate TEXT NOT NULL,
                SharesSold TEXT NOT NULL, Proceeds TEXT NOT NULL,
                CostBasisConsumed TEXT NOT NULL, RealizedGainLoss TEXT NOT NULL,
                UnmatchedShares TEXT NOT NULL DEFAULT '0',
                ImportBatchId INTEGER NULL);
            INSERT INTO Position (Id, Ticker, TargetPrice) VALUES (1, 'MU', '195.5'), (2, 'VOO', NULL);
            INSERT INTO Tx (Ticker, TradeDate, Type, Shares, Amount, Source)
            VALUES ('MU', '2026-01-05', 0, '10', '1000', 1);
            INSERT INTO Lot (PositionId, Shares, TotalCost, RemainingShares, RemainingCost, TradeDate, Source)
            VALUES (1, '10', '1000', '10', '1000', '2026-01-05', 1);
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Early_v0_database_missing_tables_migrates_cleanly()
    {
        SeedEarlyV0Database();
        var db = new Database(_dbPath);   // used to die: 'no such table: CashFlow'

        var account = Assert.Single(new AccountRepository(db).GetAll());
        var positions = new PositionRepository(db).GetAll();
        Assert.Equal(2, positions.Count);
        Assert.All(positions, p => Assert.Equal(account.Id, p.AccountId));
        Assert.Single(new LotRepository(db).GetByPosition(1, openOnly: true));
        Assert.Equal("MU", Assert.Single(new TickerTargetRepository(db).GetAll()).Ticker);
        // the skipped tables exist in v1 form afterwards and are usable
        Assert.Equal(0m, new CashFlowRepository(db).GetTotals(account.Id).Deposits);
    }

    [Fact]
    public void V0_database_with_data_migrates_to_one_reviewable_placeholder_account()
    {
        SeedV0Database(withData: true);
        var db = new Database(_dbPath);

        var accounts = new AccountRepository(db).GetAll();
        var account = Assert.Single(accounts);
        Assert.Equal("", account.AccountNumber);          // adopted by a later overlapping import
        Assert.True(account.NeedsReview);                 // prompts once on first launch
        Assert.Equal(TaxTreatment.Taxable, account.TaxTreatment);

        // every row assigned to the placeholder, ids preserved
        var positions = new PositionRepository(db).GetAll();
        Assert.Equal(2, positions.Count);
        Assert.All(positions, p => Assert.Equal(account.Id, p.AccountId));
        Assert.Equal(1, positions.Single(p => p.Ticker == "MU").Id);
        Assert.Equal("chips", positions.Single(p => p.Ticker == "MU").Notes);

        var txs = new TransactionRepository(db);
        Assert.Single(txs.GetByAccountAndTicker(account.Id, "MU"));
        Assert.Single(new LotRepository(db).GetByPosition(1, openOnly: true));
        Assert.Equal(500m, new CashFlowRepository(db).GetTotals(account.Id).Deposits);
        Assert.Equal(account.Id, new ImportBatchRepository(db).GetAll().Single().AccountId);
    }

    [Fact]
    public void V0_targets_lift_to_ticker_level()
    {
        SeedV0Database(withData: true);
        var db = new Database(_dbPath);

        var targets = new TickerTargetRepository(db).GetAll();
        var t = Assert.Single(targets);                    // only MU had a target
        Assert.Equal("MU", t.Ticker);
        Assert.Equal(195.5m, t.TargetPrice);
        Assert.Equal(TargetDirection.AtOrBelow, t.TargetDirection);
        Assert.True(t.AlertEnabled);
        Assert.Null(t.AlertTriggeredAt);
    }

    [Fact]
    public void Empty_v0_database_migrates_without_creating_an_account()
    {
        SeedV0Database(withData: false);
        var db = new Database(_dbPath);
        Assert.Empty(new AccountRepository(db).GetAll());
        Assert.Empty(new PositionRepository(db).GetAll());
    }

    [Fact]
    public void Migration_runs_once_and_fresh_databases_skip_it()
    {
        SeedV0Database(withData: true);
        new Database(_dbPath).Open().Close();              // migrate
        var reopened = new Database(_dbPath);              // second instance: no re-migration
        Assert.Single(new AccountRepository(reopened).GetAll());

        var freshPath = Path.Combine(Path.GetTempPath(), $"pt-fresh-{Guid.NewGuid():N}.db");
        try
        {
            var fresh = new Database(freshPath);
            Assert.Empty(new AccountRepository(fresh).GetAll());
            TestData.Account(fresh);                       // schema is ready for inserts
            Assert.Single(new AccountRepository(fresh).GetAll());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(freshPath);
        }
    }
}
