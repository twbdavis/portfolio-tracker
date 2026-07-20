using System.Text;
using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Import;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Core.Tests;

public class ImportServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-import-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly ImportService _svc;

    public ImportServiceTests()
    {
        _db = new Database(_dbPath);
        _svc = new ImportService(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private static Stream Csv(params string[] rows)
    {
        var text = ChaseCsvParserTests.Header + "\r\n" + string.Join("\r\n", rows);
        return new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray());
    }

    private static string Row(string date, string type, string ticker, string secType, string qty, string amount,
        string accountName = "Self-Directed", string accountNumber = "...1234") =>
        $"\"{date}\",\"{date}\",\"{date}\",\"{accountName}\",\"{accountNumber}\",\"Brokerage\",\"{type}\",\"DESC {ticker}\",\"\",\"{ticker}\",\"{secType}\",\"USD\",\"1\",\"1\",\"{qty}\",\"\",\"\",\"\",\"\",\"{amount}\",\"{amount}\",\"\",\"\",\"0\",\"\",\"\",\"0\",\"{type}\",\"\",\"0\",\"0\"";

    private ImportResult Import(string fileName, params string[] rows) =>
        _svc.Import(Csv(rows), fileName, ColumnMap.Default)!;

    [Fact]
    public void Import_creates_account_batch_positions_lots_and_reconciliation()
    {
        var r = Import("one.csv",
            Row("1/5/2026", "Buy", "MU", "Stock", "10", "-100"),
            Row("2/5/2026", "Buy", "MU", "Stock", "5", "-60"),
            Row("3/5/2026", "Sell", "MU", "Stock", "-4", "48"),
            Row("1/6/2026", "Buy", "DRAM", "Stock", "1", "-65.05"),
            Row("1/7/2026", "Dividend", "BSV", "Stock", "0", "3.35"));

        Assert.Equal(5, r.RowsImported); // 4 trades + 1 dividend cash flow
        Assert.Equal(0, r.RowsSkipped);
        Assert.Equal(0, r.RowsDeduped);
        var mu = r.Reconciliation.Single(x => x.Ticker == "MU");
        Assert.Equal(11m, mu.Shares);
        Assert.False(mu.NeedsOpeningBalance);
        Assert.Equal(1m, r.Reconciliation.Single(x => x.Ticker == "DRAM").Shares);

        var account = new AccountRepository(_db).GetByNumber("...1234")!;
        Assert.Equal(TaxTreatment.Taxable, account.TaxTreatment);   // auto-suggested default
        var batches = new ImportBatchRepository(_db).GetAll();
        var b = Assert.Single(batches);
        Assert.Equal("one.csv", b.FileName);
        Assert.Equal(account.Id, b.AccountId);
        Assert.Equal(5, b.RowsImported);
    }

    [Fact]
    public void Mixed_account_file_routes_row_by_row_into_one_batch_per_account()
    {
        var r = Import("mixed.csv",
            Row("1/5/2026", "Buy", "MU", "Stock", "10", "-100"),
            Row("1/6/2026", "Buy", "VOO", "Stock", "1", "-505.31"),
            Row("1/6/2026", "Buy", "VOO", "Stock", "0.1", "-51.00", "Self-Directed-Ret", "...5678"),
            Row("1/7/2026", "Dividend", "VOO", "Stock", "0", "1.25", "Self-Directed-Ret", "...5678"));

        Assert.Equal(4, r.RowsImported);
        Assert.Equal(2, r.BatchIds.Count);       // one batch per account

        var accounts = new AccountRepository(_db);
        var taxable = accounts.GetByNumber("...1234")!;
        var roth = accounts.GetByNumber("...5678")!;
        Assert.Equal(TaxTreatment.RothIRA, roth.TaxTreatment);   // "Ret" auto-suggest

        // Position identity = (AccountId, Ticker): VOO exists twice
        var positions = new PositionRepository(_db);
        Assert.Equal(2, positions.GetAllByTicker("VOO").Count);
        Assert.NotNull(positions.GetByAccountAndTicker(taxable.Id, "VOO"));
        Assert.NotNull(positions.GetByAccountAndTicker(roth.Id, "VOO"));

        // reconciliation grouped per account
        Assert.Equal(1m, r.Reconciliation.Single(x => x.AccountId == taxable.Id && x.Ticker == "VOO").Shares);
        Assert.Equal(0.1m, r.Reconciliation.Single(x => x.AccountId == roth.Id && x.Ticker == "VOO").Shares);

        // dividend landed in the roth account
        Assert.Equal(1.25m, new CashFlowRepository(_db).GetTotals(roth.Id).Dividends);
    }

    [Fact]
    public void New_account_callback_decides_name_and_treatment_and_can_abort()
    {
        var seen = new List<NewAccountSuggestion>();
        var r = _svc.Import(Csv(Row("1/5/2026", "Buy", "VOO", "Stock", "0.1", "-51", "Self-Directed-Ret", "...5678")),
            "a.csv", ColumnMap.Default,
            s => { seen.Add(s); return new NewAccountDecision("My Roth", TaxTreatment.RothIRA); });
        Assert.NotNull(r);
        var suggestion = Assert.Single(seen);
        Assert.Equal("...5678", suggestion.AccountNumber);
        Assert.Equal(TaxTreatment.RothIRA, suggestion.SuggestedTreatment);
        Assert.Contains("Ret", suggestion.Evidence);
        Assert.Equal("My Roth", new AccountRepository(_db).GetByNumber("...5678")!.DisplayName);

        // declining the dialog aborts before any write
        var aborted = _svc.Import(Csv(Row("1/5/2026", "Buy", "MU", "Stock", "1", "-10", "X", "...0001")),
            "b.csv", ColumnMap.Default, _ => null);
        Assert.Null(aborted);
        Assert.Null(new AccountRepository(_db).GetByNumber("...0001"));
        Assert.Empty(new TransactionRepository(_db).GetByTicker("MU"));
    }

    [Fact]
    public void Fifo_consumption_stays_strictly_within_one_account()
    {
        // Roth sells 5 VOO but only ever bought 2 there; the taxable account's
        // 10 shares must NOT be consumed
        var r = Import("iso.csv",
            Row("1/5/2026", "Buy", "VOO", "Stock", "10", "-5000"),
            Row("1/6/2026", "Buy", "VOO", "Stock", "2", "-1000", "Self-Directed-Ret", "...5678"),
            Row("2/5/2026", "Sell", "VOO", "Stock", "-5", "2600", "Self-Directed-Ret", "...5678"));

        var accounts = new AccountRepository(_db);
        var taxable = accounts.GetByNumber("...1234")!;
        var roth = accounts.GetByNumber("...5678")!;
        var positions = new PositionRepository(_db);
        var lots = new LotRepository(_db);

        var taxableOpen = lots.GetByPosition(positions.GetByAccountAndTicker(taxable.Id, "VOO")!.Id, openOnly: true);
        Assert.Equal(10m, taxableOpen.Sum(l => l.RemainingShares));   // untouched

        var rothRec = r.Reconciliation.Single(x => x.AccountId == roth.Id);
        Assert.Equal(-3m, rothRec.Shares);                            // 2 - 5
        Assert.True(rothRec.NeedsOpeningBalance);                     // oversell within the roth only
        Assert.False(r.Reconciliation.Single(x => x.AccountId == taxable.Id).NeedsOpeningBalance);
    }

    [Fact]
    public void Split_whole_and_fractional_sell_rows_import_as_separate_legitimate_rows()
    {
        var r = Import("split.csv",
            Row("6/5/2026", "Buy", "BSV", "Stock", "12.8164", "-1013"),
            Row("6/30/2026", "Sell", "BSV", "Stock", "-12", "931.96"),
            Row("6/30/2026", "Sell", "BSV", "Stock", "-0.8164", "63.40"));

        Assert.Equal(3, r.RowsImported);
        Assert.Equal(0, r.RowsDeduped);   // same day + price but different Quantity: NOT merged
        Assert.Equal(0m, r.Reconciliation.Single().Shares);
        var sales = new SaleRepository(_db).GetAll();
        Assert.Equal(2, sales.Count);
        Assert.Equal(931.96m + 63.40m, sales.Sum(s => s.Proceeds));   // net proceeds preserved
    }

    [Fact]
    public void Cost_basis_derives_from_amount_not_rounded_price()
    {
        Import("smpl.csv", Row("6/9/2026", "Buy", "SMPL", "Stock", "10", "-130.45"));
        var positions = new PositionRepository(_db);
        var account = new AccountRepository(_db).GetByNumber("...1234")!;
        var lot = new LotRepository(_db)
            .GetByPosition(positions.GetByAccountAndTicker(account.Id, "SMPL")!.Id, openOnly: true).Single();
        Assert.Equal(130.45m, lot.TotalCost);                 // the SMPL 13.045 case
        Assert.Equal(13.045m, lot.TotalCost / lot.Shares);
    }

    [Fact]
    public void Cash_flows_import_dedupe_and_total()
    {
        Import("a.csv",
            Row("1/5/2026", "BNK", "", "Other", "0", "500"),
            Row("1/7/2026", "Dividend", "BSV", "Stock", "0", "3.35"));
        var r2 = Import("b.csv",
            Row("1/5/2026", "BNK", "", "Other", "0", "500"),      // dup of a.csv
            Row("2/5/2026", "BNK", "", "Other", "0", "250"));

        Assert.Equal(1, r2.RowsImported);
        Assert.Equal(1, r2.RowsDeduped);
        var totals = new CashFlowRepository(_db).GetTotals();
        Assert.Equal(750m, totals.Deposits);
        Assert.Equal(3.35m, totals.Dividends);
    }

    [Fact]
    public void DeleteBatch_removes_its_cash_flows()
    {
        var r1 = Import("a.csv", Row("1/5/2026", "BNK", "", "Other", "0", "500"));
        Import("b.csv", Row("2/5/2026", "BNK", "", "Other", "0", "250"));
        _svc.DeleteBatch(r1.BatchIds.Single());

        var cash = new CashFlowRepository(_db);
        Assert.Equal(250m, cash.GetTotals().Deposits);
        // deleted rows' dedupe keys are gone, so re-importing the file restores them
        var r3 = Import("a2.csv", Row("1/5/2026", "BNK", "", "Other", "0", "500"));
        Assert.Equal(1, r3.RowsImported);
        Assert.Equal(750m, cash.GetTotals().Deposits);
    }

    [Fact]
    public void Reimporting_overlapping_range_dedupes()
    {
        Import("a.csv", Row("1/5/2026", "Buy", "MU", "Stock", "10", "-100"));
        var r2 = Import("b.csv",
            Row("1/5/2026", "Buy", "MU", "Stock", "10", "-100"),   // dup
            Row("2/5/2026", "Buy", "MU", "Stock", "5", "-60"));
        Assert.Equal(1, r2.RowsImported);
        Assert.Equal(1, r2.RowsDeduped);
        Assert.Equal(15m, r2.Reconciliation.Single().Shares);
    }

    [Fact]
    public void Identical_rows_in_different_accounts_do_not_dedupe()
    {
        var r = Import("both.csv",
            Row("6/12/2026", "Buy", "VOO", "Stock", "0.1", "-51.00"),
            Row("6/12/2026", "Buy", "VOO", "Stock", "0.1", "-51.00", "Self-Directed-Ret", "...5678"));
        Assert.Equal(2, r.RowsImported);
        Assert.Equal(0, r.RowsDeduped);
    }

    [Fact]
    public void Identical_rows_within_one_file_also_dedupe()
    {
        var r = Import("a.csv",
            Row("1/5/2026", "Buy", "MU", "Stock", "10", "-100"),
            Row("1/5/2026", "Buy", "MU", "Stock", "10", "-100"));
        Assert.Equal(1, r.RowsImported);
        Assert.Equal(1, r.RowsDeduped);
    }

    [Fact]
    public void DeleteBatch_removes_transactions_and_rebuilds()
    {
        var r1 = Import("a.csv", Row("1/5/2026", "Buy", "MU", "Stock", "10", "-100"));
        Import("b.csv", Row("2/5/2026", "Buy", "MU", "Stock", "5", "-60"));
        _svc.DeleteBatch(r1.BatchIds.Single());

        var account = new AccountRepository(_db).GetByNumber("...1234")!;
        var positions = new PositionRepository(_db);
        var lots = new LotRepository(_db);
        var pos = positions.GetByAccountAndTicker(account.Id, "MU")!;
        var open = lots.GetByPosition(pos.Id, openOnly: true);
        Assert.Equal(5m, open.Sum(l => l.RemainingShares));
        Assert.DoesNotContain(new ImportBatchRepository(_db).GetAll(), b => b.Id == r1.BatchIds.Single());
    }

    [Fact]
    public void Oversell_flags_ticker_and_opening_balance_resolves_it()
    {
        var r = Import("mid.csv", Row("3/5/2026", "Sell", "RCAT", "Stock", "-10", "107.14"));
        var rec = r.Reconciliation.Single();
        Assert.True(rec.NeedsOpeningBalance);
        Assert.Equal(-10m, rec.Shares);

        var manual = new ManualEntryService(_db);
        manual.AddLot(rec.AccountId, "RCAT", 25m, 200m, new DateOnly(2025, 1, 15), openingBalance: true);

        var positions = new PositionRepository(_db);
        var pos = positions.GetByAccountAndTicker(rec.AccountId, "RCAT")!;
        Assert.False(pos.NeedsOpeningBalance);
        var open = new LotRepository(_db).GetByPosition(pos.Id, openOnly: true);
        Assert.Equal(15m, open.Sum(l => l.RemainingShares));
    }

    [Fact]
    public void Manual_lot_update_and_delete_rebuild()
    {
        var account = TestData.Account(_db, "...0001", "Manual");
        var manual = new ManualEntryService(_db);
        manual.AddLot(account.Id, "NVDA", 2m, 300m, new DateOnly(2026, 1, 5), openingBalance: false);
        var txRepo = new TransactionRepository(_db);
        var tx = txRepo.GetByAccountAndTicker(account.Id, "NVDA").Single();

        manual.UpdateLot(tx.Id, 3m, 450m, new DateOnly(2026, 1, 6));
        var positions = new PositionRepository(_db);
        var lots = new LotRepository(_db);
        var open = lots.GetByPosition(positions.GetByAccountAndTicker(account.Id, "NVDA")!.Id, openOnly: true);
        Assert.Equal(3m, open.Single().RemainingShares);
        Assert.Equal(450m, open.Single().RemainingCost);

        manual.DeleteLot(tx.Id);
        Assert.Empty(lots.GetByPosition(positions.GetByAccountAndTicker(account.Id, "NVDA")!.Id, openOnly: true));
    }

    [Fact]
    public void Unrecognized_types_surface_in_result()
    {
        var r = Import("a.csv", Row("1/5/2026", "Journal", "ABC", "Stock", "1", "-1"));
        Assert.Equal(0, r.RowsImported);
        Assert.Equal(1, r.RowsSkipped);
        Assert.Equal(1, r.UnrecognizedTypes["Journal"]);
    }

    [Fact]
    public void Masked_csv_number_matches_existing_account_entered_without_the_mask()
    {
        // the user typed "1234" by hand; the CSV reports "...1234" — same account
        var existing = TestData.Account(_db, "1234", "My Account");

        var prompts = new List<NewAccountSuggestion>();
        var r = _svc.Import(Csv(Row("1/5/2026", "Buy", "MU", "Stock", "10", "-100")),
            "a.csv", ColumnMap.Default,
            s => { prompts.Add(s); return new NewAccountDecision(s.SuggestedName, s.SuggestedTreatment); })!;

        Assert.Empty(prompts);                                   // no new-account dialog
        var accounts = new AccountRepository(_db);
        Assert.Single(accounts.GetAll());                        // no duplicate created
        Assert.Equal(existing.Id, Assert.Single(new ImportBatchRepository(_db).GetAll()).AccountId);
        Assert.Equal(1, r.RowsImported);
        Assert.Equal(existing.Id, r.Reconciliation.Single().AccountId);
    }

    [Fact]
    public void Migration_placeholder_account_adopts_the_overlapping_account_number()
    {
        // pre-migration rows carry legacy (account-less) dedupe keys in a
        // placeholder account with an empty number
        var accounts = new AccountRepository(_db);
        var placeholder = new Account { AccountNumber = "", DisplayName = "My Account", CreatedAt = DateTimeOffset.UtcNow, NeedsReview = true };
        accounts.Insert(placeholder);
        new TransactionRepository(_db).Insert(new Transaction
        {
            AccountId = placeholder.Id, Ticker = "MU", TradeDate = new DateOnly(2026, 1, 5), Type = TxType.Buy,
            Shares = 10m, Amount = 100m, Source = LotSource.ChaseImport,
            DedupeKey = "1/5/2026|Buy|MU|10|-100",   // legacy format
        });

        var r = Import("overlap.csv",
            Row("1/5/2026", "Buy", "MU", "Stock", "10", "-100"),     // overlaps the legacy row
            Row("2/5/2026", "Buy", "MU", "Stock", "5", "-60"));      // new

        Assert.Equal("...1234", accounts.GetById(placeholder.Id)!.AccountNumber);  // adopted, not duplicated
        Assert.Single(accounts.GetAll());
        Assert.Equal(1, r.RowsImported);
        Assert.Equal(1, r.RowsDeduped);   // the overlapping row matched its legacy key
        Assert.Equal(15m, r.Reconciliation.Single().Shares);
    }
}
