using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Import;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tests;

public class SampleCsvTests : IDisposable
{
    private static string SamplePath =>
        Path.Combine(AppContext.BaseDirectory, "chase-transactions-sample.csv");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-sample-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Sample_headers_validate_as_chase_format()
    {
        using var stream = File.OpenRead(SamplePath);
        Assert.Null(ChaseCsvParser.ValidateHeaders(stream));
    }

    [Fact]
    public void Sample_parses_two_accounts_18_trades_4_cash_flows_and_skips_4_sweep_rows()
    {
        using var stream = File.OpenRead(SamplePath);
        var r = ChaseCsvParser.Parse(stream, ColumnMap.Default);
        Assert.Equal(18, r.Trades.Count);
        Assert.Equal(4, r.CashFlows.Count); // 3 BNK deposits + BSV dividend
        Assert.Equal(4, r.SkippedKnown);    // QACDS WDL/DBS + QDERQ WDL/DBS
        Assert.Empty(r.UnrecognizedTypes);

        Assert.Equal(2, r.Accounts.Count);
        Assert.False(r.Accounts["...1234"].SuggestIra);
        Assert.True(r.Accounts["...5678"].SuggestIra);   // "Self-Directed-Ret" + IRA sweep + IRA: pulls
    }

    [Fact]
    public void Sample_import_produces_expected_positions_per_account()
    {
        var db = new Database(_dbPath);
        var svc = new ImportService(db);
        using var stream = File.OpenRead(SamplePath);
        var result = svc.Import(stream, "chase-transactions-sample.csv", ColumnMap.Default)!;

        var accounts = new AccountRepository(db);
        var taxable = accounts.GetByNumber("...1234")!;
        var roth = accounts.GetByNumber("...5678")!;
        Assert.Equal(TaxTreatment.Taxable, taxable.TaxTreatment);
        Assert.Equal(TaxTreatment.RothIRA, roth.TaxTreatment);   // auto-suggested
        Assert.Equal(2, result.BatchIds.Count);                  // one batch per account

        decimal Shares(long accountId, string t) =>
            result.Reconciliation.Single(x => x.AccountId == accountId && x.Ticker == t).Shares;
        Assert.Equal(3.06235m, Shares(taxable.Id, "MU"));   // 5 + 0.06235 - 2
        Assert.Equal(1m, Shares(taxable.Id, "DRAM"));       // 0.5 - 0.5 + 1
        Assert.Equal(10m, Shares(taxable.Id, "RCAT"));      // 20 - 10
        Assert.Equal(1m, Shares(taxable.Id, "GOOG"));       // GOOG and GOOGL stay separate
        Assert.Equal(1m, Shares(taxable.Id, "GOOGL"));
        Assert.Equal(0m, Shares(taxable.Id, "BSV"));        // split whole+fractional sell closes it
        Assert.Equal(10m, Shares(taxable.Id, "SMPL"));
        Assert.Equal(1m, Shares(taxable.Id, "VOO"));
        Assert.Equal(0.2m, Shares(roth.Id, "VOO"));         // VOO held in BOTH accounts
        Assert.All(result.Reconciliation, x => Assert.False(x.NeedsOpeningBalance));

        var positions = new PositionRepository(db);
        var sales = new SaleRepository(db);

        // FIFO on the MU sell: 2 shares from the 6/20 lot of 5 @ 960 -> basis 1920, realized -20
        var mu = sales.GetByPosition(positions.GetByAccountAndTicker(taxable.Id, "MU")!.Id).Single();
        Assert.Equal(1920.00m, mu.CostBasisConsumed);
        Assert.Equal(-20.00m, mu.RealizedGainLoss);

        // rounded-price fractional buy: basis from Amount USD, never Price x Qty
        var smpl = new LotRepository(db)
            .GetByPosition(positions.GetByAccountAndTicker(taxable.Id, "SMPL")!.Id, openOnly: true).Single();
        Assert.Equal(130.45m, smpl.TotalCost);
        Assert.Equal(13.045m, smpl.TotalCost / smpl.Shares);

        // the split liquidation lands as two separate sale rows with NET proceeds
        var bsv = sales.GetByPosition(positions.GetByAccountAndTicker(taxable.Id, "BSV")!.Id);
        Assert.Equal(2, bsv.Count);
        Assert.Equal(931.96m + 63.40m, bsv.Sum(s => s.Proceeds));

        var cash = new CashFlowRepository(db);
        Assert.Equal(62m, cash.GetTotals(taxable.Id).Deposits);
        Assert.Equal(3.35m, cash.GetTotals(taxable.Id).Dividends);
        Assert.Equal(102.20m, cash.GetTotals(roth.Id).Deposits);  // IRA:C2026RTH pulls
    }
}
