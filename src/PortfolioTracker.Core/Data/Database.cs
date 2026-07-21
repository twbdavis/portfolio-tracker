using Microsoft.Data.Sqlite;

namespace PortfolioTracker.Core.Data;

public class Database
{
    // v1: multi-account. Account + TickerTarget tables; AccountId on Position,
    // Tx, Lot, SaleRecord, CashFlow, ImportBatch; per-ticker targets lifted out
    // of Position; WashSaleAdjustment gains Kind.
    private const int CurrentVersion = 1;

    private readonly string _connString;
    private bool _initialized;
    private readonly object _initLock = new();

    public Database(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        lock (_initLock)
        {
            if (!_initialized) { InitializeSchema(conn); _initialized = true; }
        }
        return conn;
    }

    private static void InitializeSchema(SqliteConnection conn)
    {
        var version = (long)Exec(conn, "PRAGMA user_version")!;
        if (version < 1 && TableExists(conn, "Position") && !ColumnExists(conn, "Position", "AccountId"))
            MigrateV0ToV1(conn);
        CreateSchema(conn);
        if (version < CurrentVersion)
            Exec(conn, $"PRAGMA user_version = {CurrentVersion}");
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Account (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountNumber TEXT NOT NULL UNIQUE,
                AccountNameRaw TEXT NULL,
                DisplayName TEXT NOT NULL,
                TaxTreatment INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                NeedsReview INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Position (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId INTEGER NOT NULL REFERENCES Account(Id),
                Ticker TEXT NOT NULL COLLATE NOCASE,
                Cusip TEXT NULL, Name TEXT NULL,
                NeedsOpeningBalance INTEGER NOT NULL DEFAULT 0,
                Notes TEXT NULL,
                UNIQUE (AccountId, Ticker));
            CREATE TABLE IF NOT EXISTS TickerTarget (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ticker TEXT NOT NULL COLLATE NOCASE UNIQUE,
                TargetPrice TEXT NULL,
                TargetDirection INTEGER NOT NULL DEFAULT 0,
                AlertEnabled INTEGER NOT NULL DEFAULT 0,
                AlertTriggeredAt TEXT NULL,
                Notes TEXT NULL);
            CREATE TABLE IF NOT EXISTS ImportBatch (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId INTEGER NOT NULL REFERENCES Account(Id),
                ImportedAt TEXT NOT NULL, FileName TEXT NOT NULL,
                RowsImported INTEGER NOT NULL, RowsSkipped INTEGER NOT NULL,
                RowsDeduped INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Tx (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId INTEGER NOT NULL REFERENCES Account(Id),
                Ticker TEXT NOT NULL COLLATE NOCASE,
                Cusip TEXT NULL,
                TradeDate TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Shares TEXT NOT NULL,
                Amount TEXT NOT NULL,
                Description TEXT NULL,
                Source INTEGER NOT NULL,
                ImportBatchId INTEGER NULL REFERENCES ImportBatch(Id),
                DedupeKey TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_Tx_Ticker ON Tx(Ticker);
            CREATE INDEX IF NOT EXISTS IX_Tx_Account_Ticker ON Tx(AccountId, Ticker);
            CREATE INDEX IF NOT EXISTS IX_Tx_Batch ON Tx(ImportBatchId);
            CREATE TABLE IF NOT EXISTS Lot (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId INTEGER NOT NULL REFERENCES Account(Id),
                PositionId INTEGER NOT NULL REFERENCES Position(Id),
                Shares TEXT NOT NULL, TotalCost TEXT NOT NULL,
                RemainingShares TEXT NOT NULL, RemainingCost TEXT NOT NULL,
                TradeDate TEXT NOT NULL,
                Source INTEGER NOT NULL,
                ImportBatchId INTEGER NULL, ParentLotId INTEGER NULL, SourceTxId INTEGER NULL);
            CREATE INDEX IF NOT EXISTS IX_Lot_Position ON Lot(PositionId);
            CREATE TABLE IF NOT EXISTS SaleRecord (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId INTEGER NOT NULL REFERENCES Account(Id),
                PositionId INTEGER NOT NULL REFERENCES Position(Id),
                TradeDate TEXT NOT NULL,
                SharesSold TEXT NOT NULL, Proceeds TEXT NOT NULL,
                CostBasisConsumed TEXT NOT NULL, RealizedGainLoss TEXT NOT NULL,
                UnmatchedShares TEXT NOT NULL DEFAULT '0',
                ImportBatchId INTEGER NULL);
            CREATE INDEX IF NOT EXISTS IX_Sale_Position ON SaleRecord(PositionId);
            CREATE TABLE IF NOT EXISTS CashFlow (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId INTEGER NOT NULL REFERENCES Account(Id),
                TradeDate TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Ticker TEXT NULL COLLATE NOCASE,
                Amount TEXT NOT NULL,
                Description TEXT NULL,
                ImportBatchId INTEGER NULL REFERENCES ImportBatch(Id),
                DedupeKey TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_CashFlow_Batch ON CashFlow(ImportBatchId);
            CREATE TABLE IF NOT EXISTS PriceCache (
                Ticker TEXT PRIMARY KEY COLLATE NOCASE,
                LastPrice TEXT NOT NULL, FetchedAt TEXT NOT NULL,
                PrevClose TEXT NULL);
            CREATE TABLE IF NOT EXISTS ResearchCache (
                Ticker TEXT NOT NULL COLLATE NOCASE,
                Kind TEXT NOT NULL,
                FetchedAt TEXT NOT NULL,
                Json TEXT NOT NULL,
                PRIMARY KEY (Ticker, Kind));
            CREATE TABLE IF NOT EXISTS LotConsumption (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PositionId INTEGER NOT NULL REFERENCES Position(Id),
                SaleRecordId INTEGER NOT NULL REFERENCES SaleRecord(Id),
                LotId INTEGER NOT NULL REFERENCES Lot(Id),
                SharesConsumed TEXT NOT NULL,
                CostBasisConsumed TEXT NOT NULL,
                Proceeds TEXT NOT NULL,
                LotAcquisitionDate TEXT NOT NULL,
                SaleTradeDate TEXT NOT NULL,
                HoldingDays INTEGER NOT NULL,
                Term INTEGER NOT NULL,
                GainLoss TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_LotConsumption_Position ON LotConsumption(PositionId);
            CREATE TABLE IF NOT EXISTS WashSaleAdjustment (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PositionId INTEGER NOT NULL REFERENCES Position(Id),
                LotConsumptionId INTEGER NOT NULL REFERENCES LotConsumption(Id),
                ReplacementLotId INTEGER NOT NULL REFERENCES Lot(Id),
                SharesMatched TEXT NOT NULL,
                DisallowedLoss TEXT NOT NULL,
                Kind INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WashSale_Position ON WashSaleAdjustment(PositionId);
            CREATE TABLE IF NOT EXISTS TaxSetting (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS TaxCarryforward (
                Year INTEGER PRIMARY KEY,
                ShortTerm TEXT NOT NULL,
                LongTerm TEXT NOT NULL,
                IsManual INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS WatchlistItem (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ticker TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Name TEXT NULL,
                AddedAt TEXT NOT NULL,
                Thesis TEXT NULL,
                Conviction INTEGER NOT NULL DEFAULT 3,
                TargetBuyPrice TEXT NULL,
                AlertEnabled INTEGER NOT NULL DEFAULT 0,
                AlertTriggeredAt TEXT NULL,
                Notes TEXT NULL);
            CREATE TABLE IF NOT EXISTS Universe (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                IsSeeded INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS UniverseMember (
                UniverseId INTEGER NOT NULL REFERENCES Universe(Id) ON DELETE CASCADE,
                Ticker TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (UniverseId, Ticker));
            CREATE TABLE IF NOT EXISTS TickerClassification (
                Ticker TEXT PRIMARY KEY COLLATE NOCASE,
                Sector TEXT NULL,
                Industry TEXT NULL,
                Source INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS TickerMetrics (
                Ticker TEXT PRIMARY KEY COLLATE NOCASE,
                AsOf TEXT NOT NULL,
                MarketCap TEXT NULL, PE TEXT NULL, PS TEXT NULL, PB TEXT NULL,
                RevenueGrowthYoY TEXT NULL, EpsGrowth TEXT NULL,
                GrossMargin TEXT NULL, NetMargin TEXT NULL, ROE TEXT NULL,
                Beta TEXT NULL, Wk52High TEXT NULL, Wk52Low TEXT NULL);
            CREATE TABLE IF NOT EXISTS RecommendationSnapshot (
                Ticker TEXT NOT NULL COLLATE NOCASE,
                Month TEXT NOT NULL,
                StrongBuy INTEGER NOT NULL, Buy INTEGER NOT NULL, Hold INTEGER NOT NULL,
                Sell INTEGER NOT NULL, StrongSell INTEGER NOT NULL,
                PRIMARY KEY (Ticker, Month));
            CREATE TABLE IF NOT EXISTS DailyClose (
                Ticker TEXT NOT NULL COLLATE NOCASE,
                Date TEXT NOT NULL,
                Close TEXT NOT NULL,
                Source INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (Ticker, Date));
            CREATE TABLE IF NOT EXISTS SignalHit (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ticker TEXT NOT NULL COLLATE NOCASE,
                SignalKind TEXT NOT NULL,
                FiredAt TEXT NOT NULL,
                InputsJson TEXT NOT NULL,
                Dismissed INTEGER NOT NULL DEFAULT 0,
                DismissedAt TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_SignalHit_Ticker_Kind ON SignalHit(Ticker, SignalKind);
            CREATE TABLE IF NOT EXISTS AppState (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS CrawlStamp (
                Ticker TEXT NOT NULL COLLATE NOCASE,
                Kind INTEGER NOT NULL,
                FetchedAt TEXT NOT NULL,
                PRIMARY KEY (Ticker, Kind));
            """;
        cmd.ExecuteNonQuery();
        AddColumnIfMissing(conn, "PriceCache", "PrevClose", "TEXT NULL");
    }

    /// Forward-only, automatic, single transaction. Pre-account rows cannot
    /// know their Chase account number, so everything lands in one placeholder
    /// account (AccountNumber "") flagged NeedsReview; the first import whose
    /// rows overlap the existing dedupe keys adopts the real number.
    private static void MigrateV0ToV1(SqliteConnection conn)
    {
        // rebuilding Position requires dropping a table other tables reference;
        // the pragma only takes effect outside a transaction
        Exec(conn, "PRAGMA foreign_keys = OFF");
        try
        {
            MigrateV0ToV1Body(conn);
        }
        finally
        {
            Exec(conn, "PRAGMA foreign_keys = ON");
        }
    }

    private static void MigrateV0ToV1Body(SqliteConnection conn)
    {
        // early-v0 databases predate some tables; migrate only what exists —
        // CreateSchema creates the rest in v1 form immediately afterwards.
        // Existence checks run before BeginTransaction: Microsoft.Data.Sqlite
        // throws if a command doesn't carry the active transaction.
        var existing = new[] { "Tx", "Lot", "SaleRecord", "CashFlow", "ImportBatch" }
            .Where(t => TableExists(conn, t)).ToHashSet();
        var hasWashSale = TableExists(conn, "WashSaleAdjustment");

        using var txn = conn.BeginTransaction();

        Exec(conn, """
            CREATE TABLE Account (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountNumber TEXT NOT NULL UNIQUE,
                AccountNameRaw TEXT NULL,
                DisplayName TEXT NOT NULL,
                TaxTreatment INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                NeedsReview INTEGER NOT NULL DEFAULT 0)
            """, txn);

        // Position always exists — it's the migration trigger
        var hasData = new[] { "Position", "Tx", "CashFlow", "ImportBatch" }
            .Where(t => t == "Position" || existing.Contains(t))
            .Sum(t => (long)Exec(conn, $"SELECT COUNT(*) FROM {t}", txn)!) > 0;
        long accountId = 0;
        if (hasData)
        {
            Exec(conn, $"""
                INSERT INTO Account (AccountNumber, DisplayName, TaxTreatment, CreatedAt, NeedsReview)
                VALUES ('', 'My Account', 0, '{DateTimeOffset.Now:o}', 1)
                """, txn);
            accountId = (long)Exec(conn, "SELECT last_insert_rowid()", txn)!;
        }

        // targets move to ticker level; legacy tickers were unique so there are
        // no conflicts to resolve
        Exec(conn, """
            CREATE TABLE TickerTarget (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ticker TEXT NOT NULL COLLATE NOCASE UNIQUE,
                TargetPrice TEXT NULL,
                TargetDirection INTEGER NOT NULL DEFAULT 0,
                AlertEnabled INTEGER NOT NULL DEFAULT 0,
                AlertTriggeredAt TEXT NULL,
                Notes TEXT NULL);
            INSERT INTO TickerTarget (Ticker, TargetPrice, TargetDirection, AlertEnabled, AlertTriggeredAt)
            SELECT Ticker, TargetPrice, TargetDirection, AlertEnabled, AlertTriggeredAt
            FROM Position WHERE TargetPrice IS NOT NULL
            """, txn);

        // rebuild Position with (AccountId, Ticker) identity, preserving Ids so
        // Lot/SaleRecord/LotConsumption/WashSaleAdjustment references stay
        // valid. Copy-drop-rename (never rename the old table) so SQLite's
        // ALTER TABLE reference rewriting can't point child tables at a
        // temporary name.
        Exec(conn, $"""
            CREATE TABLE Position_v1 (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId INTEGER NOT NULL REFERENCES Account(Id),
                Ticker TEXT NOT NULL COLLATE NOCASE,
                Cusip TEXT NULL, Name TEXT NULL,
                NeedsOpeningBalance INTEGER NOT NULL DEFAULT 0,
                Notes TEXT NULL,
                UNIQUE (AccountId, Ticker));
            INSERT INTO Position_v1 (Id, AccountId, Ticker, Cusip, Name, NeedsOpeningBalance, Notes)
            SELECT Id, {accountId}, Ticker, Cusip, Name, NeedsOpeningBalance, Notes FROM Position;
            DROP TABLE Position;
            ALTER TABLE Position_v1 RENAME TO Position
            """, txn);

        foreach (var table in new[] { "Tx", "Lot", "SaleRecord", "CashFlow", "ImportBatch" }
                     .Where(existing.Contains))
            Exec(conn, $"""
                ALTER TABLE {table} ADD COLUMN AccountId INTEGER NOT NULL DEFAULT 0;
                UPDATE {table} SET AccountId = {accountId}
                """, txn);

        if (hasWashSale)
            Exec(conn, "ALTER TABLE WashSaleAdjustment ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0", txn);
        txn.Commit();
    }

    private static object? Exec(SqliteConnection conn, string sql, SqliteTransaction? txn = null)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t";
        cmd.Parameters.AddWithValue("@t", table);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=@c";
        cmd.Parameters.AddWithValue("@c", column);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        if (ColumnExists(conn, table, column)) return;
        var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }
}
