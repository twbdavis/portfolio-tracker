using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;
using PortfolioTracker.Core.Tax;

namespace PortfolioTracker.Core.Tests;

/// End-to-end over a real SQLite file: manual trades -> rebuild -> consumptions,
/// wash adjustments (all flavors), year summary, carryforward chain, 8949 export.
public class TaxServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-tax-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly TaxService _tax;
    private readonly TransactionRepository _txs;
    private readonly Rebuilder _rebuilder;
    private readonly Account _taxable;

    public TaxServiceTests()
    {
        _db = new Database(_dbPath);
        _tax = new TaxService(_db);
        _txs = new TransactionRepository(_db);
        _rebuilder = new Rebuilder(new AccountRepository(_db), new PositionRepository(_db), _txs,
            new LotRepository(_db), new SaleRepository(_db),
            new LotConsumptionRepository(_db), new WashSaleAdjustmentRepository(_db));
        _taxable = TestData.Account(_db, "...1234", "Taxable");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private void Trade(string ticker, DateOnly date, TxType type, decimal shares, decimal amount, Account? account = null)
    {
        account ??= _taxable;
        _txs.Insert(new Transaction
        { AccountId = account.Id, Ticker = ticker, TradeDate = date, Type = type, Shares = shares, Amount = amount, Source = LotSource.Manual });
        _rebuilder.RebuildTicker(account.Id, ticker);
    }

    private Position Pos(string ticker, Account? account = null) =>
        new PositionRepository(_db).GetByAccountAndTicker((account ?? _taxable).Id, ticker)!;

    [Fact]
    public void Rebuild_persists_consumptions_and_wash_adjustments()
    {
        Trade("MU", new(2026, 1, 5), TxType.Buy, 10m, 1000m);
        Trade("MU", new(2026, 2, 2), TxType.Sell, 10m, 600m);
        Trade("MU", new(2026, 2, 10), TxType.Buy, 10m, 550m);

        var cons = new LotConsumptionRepository(_db).GetAll();
        var c = Assert.Single(cons);
        Assert.Equal(10m, c.SharesConsumed);
        Assert.Equal(-400m, c.GainLoss);
        Assert.Equal(HoldingTerm.ShortTerm, c.Term);
        Assert.Equal(28, c.HoldingDays);

        var wash = new WashSaleAdjustmentRepository(_db).GetAll();
        var w = Assert.Single(wash);
        Assert.Equal(c.Id, w.LotConsumptionId);
        Assert.Equal(400m, w.DisallowedLoss);
        Assert.Equal(WashKind.SameAccount, w.Kind);
    }

    [Fact]
    public void Backfill_replays_existing_sales_into_consumptions()
    {
        Trade("MU", new(2026, 1, 5), TxType.Buy, 10m, 1000m);
        Trade("MU", new(2026, 2, 2), TxType.Sell, 4m, 500m);
        // wipe the derived table to simulate a database from before the tax module
        new LotConsumptionRepository(_db).ReplaceForPosition(Pos("MU").Id, new List<LotConsumption>());

        Assert.True(_tax.NeedsBackfill(new SaleRepository(_db)));
        _tax.RebuildAll();
        Assert.False(_tax.NeedsBackfill(new SaleRepository(_db)));
        Assert.Single(new LotConsumptionRepository(_db).GetAll());
    }

    [Fact]
    public void Year_data_nets_and_estimates_with_wash_adjustment()
    {
        _tax.Settings.SetFilingStatus(FilingStatus.Single);
        _tax.Settings.SetOtherIncome(66_100m);
        Trade("MU", new(2026, 1, 5), TxType.Buy, 10m, 1000m);
        Trade("MU", new(2026, 3, 2), TxType.Sell, 10m, 1500m);   // +500 ST
        Trade("GLXY", new(2026, 1, 5), TxType.Buy, 10m, 1000m);
        Trade("GLXY", new(2026, 2, 2), TxType.Sell, 10m, 600m);  // -400 loss...
        Trade("GLXY", new(2026, 2, 10), TxType.Buy, 10m, 550m);  // ...fully washed

        var data = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());
        Assert.Equal(500m, data.YearShortTerm);                  // the GLXY loss is disallowed
        Assert.Equal(0m, data.YearLongTerm);
        Assert.Equal(400m, data.TotalDisallowed);
        Assert.Equal(0m, data.PermanentlyLost);                  // same-account wash: deferred, not destroyed
        Assert.Equal(500m, data.Netting.TaxableShortTerm);
        // 500 ST stacked on 50,000 taxable crosses the 12% -> 22% boundary at 50,400
        Assert.Equal(400m * 0.12m + 100m * 0.22m, data.Estimate.Attributable);
    }

    // ---- IRA exclusion -------------------------------------------------------

    [Fact]
    public void Ira_sales_and_dividends_are_excluded_from_tax_totals()
    {
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        Trade("MU", new(2026, 1, 5), TxType.Buy, 10m, 1000m);
        Trade("MU", new(2026, 3, 2), TxType.Sell, 10m, 1500m);           // +500 taxable
        Trade("VOO", new(2026, 1, 5), TxType.Buy, 2m, 1000m, roth);
        Trade("VOO", new(2026, 3, 2), TxType.Sell, 2m, 1400m, roth);     // +400 in the Roth: invisible

        var cash = new CashFlowRepository(_db);
        cash.Insert(new CashFlow { AccountId = _taxable.Id, TradeDate = new(2026, 3, 1), Type = CashFlowType.Dividend, Ticker = "MU", Amount = 10m });
        cash.Insert(new CashFlow { AccountId = roth.Id, TradeDate = new(2026, 3, 1), Type = CashFlowType.Dividend, Ticker = "VOO", Amount = 99m });

        var data = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());
        Assert.Equal(500m, data.YearShortTerm);          // Roth gain absent
        Assert.All(data.Events, e => Assert.Equal(_taxable.Id, e.AccountId));
        Assert.Equal(10m, data.GrossDividends);          // Roth dividend absent
        Assert.Equal(1, data.RetirementAccountsExcluded);
        Assert.Equal(500m, _tax.GetRealizedAdjusted(2026));

        // but the Roth data is still tracked for portfolio purposes
        Assert.Single(new SaleRepository(_db).GetByPosition(Pos("VOO", roth).Id));
    }

    [Fact]
    public void Account_filter_scopes_events_and_dividends_to_one_taxable_account()
    {
        var second = TestData.Account(_db, "...0002", "Second taxable");
        Trade("MU", new(2026, 1, 5), TxType.Buy, 10m, 1000m);
        Trade("MU", new(2026, 3, 2), TxType.Sell, 10m, 1500m);            // +500 in ...1234
        Trade("GLXY", new(2026, 1, 5), TxType.Buy, 10m, 1000m, second);
        Trade("GLXY", new(2026, 3, 2), TxType.Sell, 10m, 1200m, second);  // +200 in ...0002

        var all = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());
        Assert.Equal(700m, all.YearShortTerm);
        var filtered = _tax.GetYearData(2026, TaxBracketsFile.Seed2026(), second.Id);
        Assert.Equal(200m, filtered.YearShortTerm);
        Assert.All(filtered.Events, e => Assert.Equal(second.Id, e.AccountId));
    }

    // ---- cross-account wash: both flavors -----------------------------------

    [Fact]
    public void Taxable_to_taxable_cross_account_wash_defers_the_loss_into_the_other_account()
    {
        var second = TestData.Account(_db, "...0002", "Second taxable");
        Trade("GLXY", new(2026, 1, 5), TxType.Buy, 10m, 1000m);
        Trade("GLXY", new(2026, 2, 2), TxType.Sell, 10m, 600m);            // -400 loss in ...1234
        Trade("GLXY", new(2026, 2, 10), TxType.Buy, 10m, 550m, second);    // replacement in ...0002

        var w = Assert.Single(new WashSaleAdjustmentRepository(_db).GetAll());
        Assert.Equal(WashKind.CrossAccountTaxable, w.Kind);
        Assert.Equal(400m, w.DisallowedLoss);

        var result = _tax.ComputeForTicker("GLXY");
        var view = Assert.Single(result.Views);
        Assert.Equal(400m, view.CrossAccountDisallowed);
        Assert.Equal(0m, view.IraDisallowed);
        Assert.Equal(0m, view.AdjustedGainLoss);                           // deferred, not deductible now

        // basis adjusts on the replacement lot in ITS account
        var replacementLot = new LotRepository(_db)
            .GetByPosition(Pos("GLXY", second).Id, openOnly: true).Single();
        Assert.Equal(400m, result.OpenLots[replacementLot.Id].BasisAdjustment);
        Assert.Equal(0m, result.PermanentlyDisallowed);
    }

    [Fact]
    public void Ira_replacement_purchase_destroys_the_loss_permanently()
    {
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        Trade("VOO", new(2026, 1, 5), TxType.Buy, 10m, 5000m);
        Trade("VOO", new(2026, 2, 2), TxType.Sell, 10m, 4600m);            // -400 taxable loss
        Trade("VOO", new(2026, 2, 10), TxType.Buy, 10m, 4500m, roth);      // Rev. Rul. 2008-5

        var w = Assert.Single(new WashSaleAdjustmentRepository(_db).GetAll());
        Assert.Equal(WashKind.IraPermanent, w.Kind);
        Assert.Equal(400m, w.DisallowedLoss);

        var result = _tax.ComputeForTicker("VOO");
        var view = Assert.Single(result.Views, v => v.Source.SaleTradeDate == new DateOnly(2026, 2, 2));
        Assert.Equal(400m, view.IraDisallowed);
        Assert.Equal(0m, view.AdjustedGainLoss);                           // not deductible
        Assert.Equal(400m, result.PermanentlyDisallowed);

        // NO basis adjustment anywhere — the deduction is destroyed
        var rothLot = new LotRepository(_db).GetByPosition(Pos("VOO", roth).Id, openOnly: true).Single();
        Assert.Equal(0m, result.OpenLots[rothLot.Id].BasisAdjustment);

        var data = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());
        Assert.Equal(400m, data.PermanentlyLost);
    }

    [Fact]
    public void Ira_account_losses_are_never_wash_processed()
    {
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        Trade("VOO", new(2026, 1, 5), TxType.Buy, 10m, 5000m, roth);
        Trade("VOO", new(2026, 2, 2), TxType.Sell, 10m, 4600m, roth);      // loss inside the Roth
        Trade("VOO", new(2026, 2, 10), TxType.Buy, 10m, 4500m, roth);      // rebuy inside the Roth

        Assert.Empty(new WashSaleAdjustmentRepository(_db).GetAll());      // not a taxable event
        Assert.Empty(_tax.GetAllEvents());
    }

    [Fact]
    public void Proactive_ira_wash_warning_names_the_recurring_pattern()
    {
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        Trade("VOO", new(2026, 5, 20), TxType.Buy, 1m, 505.31m);           // taxable holding
        Trade("VOO", new(2026, 6, 12), TxType.Buy, 0.1m, 51m, roth);       // recurring Roth buys
        Trade("VOO", new(2026, 6, 26), TxType.Buy, 0.1m, 51.20m, roth);

        var risk = _tax.GetIraWashRisk("VOO", new DateOnly(2026, 7, 9), "...1234");
        Assert.NotNull(risk);
        Assert.True(risk.Recurring);                                       // 14-day cadence detected
        Assert.Contains("Roth IRA ...5678", risk.Warning);
        Assert.Contains("6/12 and 6/26", risk.Warning);
        Assert.Contains("permanently destroys the loss", risk.Warning);
        // next predicted buy ~7/10; safe after predicted + 31
        Assert.True(risk.SafeToSellOn > new DateOnly(2026, 7, 26));

        Assert.Null(_tax.GetIraWashRisk("MU", new DateOnly(2026, 7, 9)));  // no IRA activity
    }

    [Fact]
    public void Rebuild_tax_data_stays_correct_under_multiple_accounts_and_treatments()
    {
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        Trade("VOO", new(2026, 1, 5), TxType.Buy, 10m, 5000m);
        Trade("VOO", new(2026, 2, 2), TxType.Sell, 10m, 4600m);
        Trade("VOO", new(2026, 2, 10), TxType.Buy, 10m, 4500m, roth);
        var before = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());

        var tickers = _tax.RebuildAll();
        Assert.Contains("VOO", tickers);
        var after = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());
        Assert.Equal(before.YearShortTerm, after.YearShortTerm);
        Assert.Equal(before.PermanentlyLost, after.PermanentlyLost);
        Assert.Equal(WashKind.IraPermanent, Assert.Single(new WashSaleAdjustmentRepository(_db).GetAll()).Kind);
    }

    // --------------------------------------------------------------------------

    [Fact]
    public void Carryforward_chains_from_prior_year_and_manual_override_wins()
    {
        // 2025: a big short-term loss
        Trade("MU", new(2025, 1, 5), TxType.Buy, 10m, 10_000m);
        Trade("MU", new(2025, 6, 1), TxType.Sell, 10m, 2_000m);  // -8,000 ST
        // 2026: a short-term gain
        Trade("MU", new(2026, 2, 1), TxType.Buy, 10m, 1_000m);
        Trade("MU", new(2026, 5, 1), TxType.Sell, 10m, 4_000m);  // +3,000 ST

        var data = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());
        Assert.Equal(5_000m, data.CarryInShortTerm);             // 8,000 - 3,000 offset in 2025
        Assert.Equal(-2_000m, data.Netting.NetShortTerm);        // 3,000 gain - 5,000 carry-in
        Assert.Equal(2_000m, data.Netting.OrdinaryOffset);
        Assert.Equal(0m, data.Netting.CarryforwardShortTerm);

        // user's real return says only 1,000 carried forward
        _tax.SetManualCarryforward(2025, 1_000m, 0m);
        var revised = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());
        Assert.True(revised.CarryInIsManual);
        Assert.Equal(1_000m, revised.CarryInShortTerm);
        Assert.Equal(2_000m, revised.Netting.TaxableShortTerm);
    }

    [Fact]
    public void Dividends_group_by_ticker_for_the_year()
    {
        var cash = new CashFlowRepository(_db);
        cash.Insert(new CashFlow { AccountId = _taxable.Id, TradeDate = new(2026, 3, 1), Type = CashFlowType.Dividend, Ticker = "VOO", Amount = 12.34m });
        cash.Insert(new CashFlow { AccountId = _taxable.Id, TradeDate = new(2026, 6, 1), Type = CashFlowType.Dividend, Ticker = "VOO", Amount = 13.66m });
        cash.Insert(new CashFlow { AccountId = _taxable.Id, TradeDate = new(2025, 12, 1), Type = CashFlowType.Dividend, Ticker = "VOO", Amount = 99m });
        cash.Insert(new CashFlow { AccountId = _taxable.Id, TradeDate = new(2026, 1, 15), Type = CashFlowType.Deposit, Amount = 500m });

        var data = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());
        Assert.Equal(26m, data.GrossDividends);
        var (ticker, amount) = Assert.Single(data.DividendsByTicker);
        Assert.Equal("VOO", ticker);
        Assert.Equal(26m, amount);
    }

    [Fact]
    public void Form8949_export_splits_terms_names_accounts_and_flags_wash_with_code_W()
    {
        Trade("MU", new(2024, 6, 1), TxType.Buy, 10m, 1000m);
        Trade("MU", new(2026, 3, 2), TxType.Sell, 10m, 1800m);   // long-term gain
        Trade("GLXY", new(2026, 1, 5), TxType.Buy, 10m, 1000m);
        Trade("GLXY", new(2026, 2, 2), TxType.Sell, 10m, 600m);  // short-term loss
        Trade("GLXY", new(2026, 2, 10), TxType.Buy, 10m, 550m);  // washes it

        var data = _tax.GetYearData(2026, TaxBracketsFile.Seed2026());
        var csv = Form8949Export.BuildCsv(2026, data.Events);
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var stHeader = lines.IndexOf("SHORT-TERM");
        var ltHeader = lines.IndexOf("LONG-TERM");
        Assert.True(stHeader >= 0 && ltHeader > stHeader);

        var washRow = lines.Single(l => l.Contains("GLXY"));
        Assert.True(lines.IndexOf(washRow) > stHeader && lines.IndexOf(washRow) < ltHeader);
        // Description,Account,Acquired,Sold,Proceeds,Basis,Code,Adjustment,GainLoss
        Assert.Equal(["10 sh GLXY", "Taxable", "1/5/2026", "2/2/2026", "600.00", "1000.00", "W", "400.00", "0.00"],
            washRow.Split(','));

        var ltRow = lines.Single(l => l.Contains("MU"));
        Assert.True(lines.IndexOf(ltRow) > ltHeader);
        Assert.Equal(["10 sh MU", "Taxable", "6/1/2024", "3/2/2026", "1800.00", "1000.00", "", "", "800.00"],
            ltRow.Split(','));
    }

    [Fact]
    public void Active_wash_risks_list_recent_losses_and_expire_after_30_days()
    {
        Trade("GLXY", new(2026, 5, 5), TxType.Buy, 10m, 1000m);
        Trade("GLXY", new(2026, 6, 9), TxType.Sell, 10m, 958.80m);   // -41.20 loss
        Trade("MU", new(2026, 1, 5), TxType.Buy, 10m, 1000m);
        Trade("MU", new(2026, 6, 20), TxType.Sell, 10m, 1400m);      // gain: never at risk

        var risks = _tax.GetActiveWashRisks(new(2026, 7, 1));
        var risk = Assert.Single(risks);
        Assert.Equal("GLXY", risk.Ticker);
        Assert.Equal(41.20m, risk.LossAtRisk);
        Assert.Equal(new DateOnly(2026, 6, 9), risk.LatestSale);
        Assert.Equal(new DateOnly(2026, 7, 10), risk.SafeToBuyOn);   // 6/9 + 31 days
        Assert.Empty(_tax.GetActiveWashRisks(new(2026, 7, 10)));     // window over
    }

    [Fact]
    public void Buy_wash_warning_fires_inside_the_window_and_clears_after()
    {
        Trade("GLXY", new(2026, 5, 5), TxType.Buy, 10m, 1000m);
        Trade("GLXY", new(2026, 6, 9), TxType.Sell, 10m, 958.80m);   // -41.20 loss

        var warning = _tax.BuyWashWarning("GLXY", new(2026, 7, 1));
        Assert.NotNull(warning);
        Assert.Contains("$41.20", warning);
        Assert.Contains("7/9", warning);                              // window end 6/9 + 30
        Assert.Null(_tax.BuyWashWarning("GLXY", new(2026, 7, 10)));   // window passed
        Assert.Null(_tax.BuyWashWarning("MU", new(2026, 7, 1)));      // no such position
    }

    [Fact]
    public void Sell_simulator_previews_lots_terms_wash_and_long_term_callout()
    {
        Trade("MU", new(2025, 8, 1), TxType.Buy, 10m, 1000m);    // long-term by 10/1/2026
        Trade("MU", new(2026, 5, 1), TxType.Buy, 10m, 2000m);    // short-term
        var lots = new LotRepository(_db).GetByPosition(Pos("MU").Id, openOnly: false);
        var wash = _tax.ComputeForTicker("MU");

        var sim = SellSimulator.Simulate(lots, wash, 15m, 300m, new(2026, 7, 8),
            ytdShortTerm: 0m, ytdLongTerm: 0m,
            TaxBracketsFile.Seed2026().Years[2026], FilingStatus.Single, otherIncome: 66_100m);

        Assert.Equal(2, sim.Portions.Count);
        var older = sim.Portions[0];
        var newer = sim.Portions[1];
        Assert.Equal(10m, older.Shares);
        Assert.Equal(HoldingTerm.ShortTerm, older.Term);         // 8/1/2025 not yet long on 7/8/2026
        Assert.Equal(5m, newer.Shares);
        Assert.Equal(HoldingTerm.ShortTerm, newer.Term);
        Assert.Equal(1000m, older.Basis);                        // proceeds 3000 -> +2000
        Assert.Equal(1000m, newer.Basis);                        // 5 sh at 200 -> proceeds 1500, +500
        Assert.Equal(2_500m, sim.ShortTermGainLoss);

        // both portions are ST gains -> two callouts; the 8/1/2025 lot flips on 8/2/2026
        Assert.Equal(2, sim.LongTermCallouts.Count);
        Assert.Equal(new DateOnly(2026, 8, 2), sim.LongTermCallouts[0].Date);
        Assert.True(sim.LongTermCallouts[0].EstimatedSavings > 0m);
        Assert.Null(sim.WashWarning);                            // selling at a gain
        Assert.Null(sim.IraWashWarning);

        // estimated tax: 2,500 ST stacked on 50,000 taxable -> 400 @ 12% + 2,100 @ 22%
        Assert.Equal(400m * 0.12m + 2_100m * 0.22m, sim.EstimatedTaxOnSale);
    }

    [Fact]
    public void Sell_simulator_warns_when_recent_purchase_would_wash_the_loss()
    {
        Trade("MU", new(2026, 1, 5), TxType.Buy, 10m, 5000m);
        Trade("MU", new(2026, 7, 1), TxType.Buy, 5m, 2500m);     // 7 days before the contemplated sale
        var lots = new LotRepository(_db).GetByPosition(Pos("MU").Id, openOnly: false);

        var sim = SellSimulator.Simulate(lots, _tax.ComputeForTicker("MU"),
            10m, 300m, new(2026, 7, 8), 0m, 0m,
            TaxBracketsFile.Seed2026().Years[2026], FilingStatus.Single, 66_100m);

        Assert.True(sim.Portions[0].GainLoss < 0m);              // 10 sh basis 5000, proceeds 3000
        Assert.NotNull(sim.WashWarning);
        Assert.Contains("disallowed", sim.WashWarning);
    }

    [Fact]
    public void Sell_simulator_distinguishes_cross_account_and_ira_flavors()
    {
        var roth = TestData.Account(_db, "...5678", "Roth", TaxTreatment.RothIRA);
        var second = TestData.Account(_db, "...0002", "Second taxable");
        Trade("VOO", new(2026, 1, 5), TxType.Buy, 10m, 5000m);              // will sell at a loss
        Trade("VOO", new(2026, 6, 20), TxType.Buy, 2m, 1000m, second);      // other-taxable window buy
        Trade("VOO", new(2026, 6, 26), TxType.Buy, 1m, 512m, roth);         // IRA window buy

        var today = new DateOnly(2026, 7, 8);
        var lots = new LotRepository(_db).GetByPosition(Pos("VOO").Id, openOnly: false);
        var otherTaxable = SellSimulator.ReplacementSharesInWindow(
            new LotRepository(_db).GetByPosition(Pos("VOO", second).Id, openOnly: false), today);
        var ira = SellSimulator.ReplacementSharesInWindow(
            new LotRepository(_db).GetByPosition(Pos("VOO", roth).Id, openOnly: false), today);
        var context = new CrossAccountWashContext
        {
            OtherTaxableReplacementShares = otherTaxable,
            IraReplacementShares = ira,
            IraWarning = _tax.GetIraWashRisk("VOO", today, "Taxable")?.Warning,
        };

        var sim = SellSimulator.Simulate(lots, _tax.ComputeForTicker("VOO"),
            10m, 300m, today, 0m, 0m,
            TaxBracketsFile.Seed2026().Years[2026], FilingStatus.Single, 66_100m, context);

        Assert.NotNull(sim.WashWarning);
        Assert.Contains("cross-account", sim.WashWarning);
        Assert.Contains("NOT appear on either 1099-B", sim.WashWarning);
        Assert.NotNull(sim.IraWashWarning);
        Assert.Contains("permanently", sim.IraWashWarning);
    }
}
