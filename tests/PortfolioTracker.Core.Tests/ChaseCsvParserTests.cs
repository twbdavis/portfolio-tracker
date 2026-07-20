using System.Text;
using PortfolioTracker.Core.Import;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tests;

public class ChaseCsvParserTests
{
    public const string Header = "Trade Date,Post Date,Settlement Date,Account Name,Account Number,Account Type,Type,Description,Cusip,Ticker,Security Type,Local Currency,Price USD,Price Local,Quantity,G/L Short USD,G/L Short Local,G/L Long USDs,G/L Long Local,Amount USD,Amount Local,Income USD,Income Local,Balance,Commissions USD,Commissions Local,Tran Code,Tran Code Description,Broker,Check Number,Tax Withheld";

    private static string Row(string tradeDate, string type, string desc, string cusip, string ticker,
        string secType, string price, string qty, string amount,
        string accountName = "Self-Directed", string accountNumber = "...1234") =>
        $"\"{tradeDate}\",\"{tradeDate}\",\"{tradeDate}\",\"{accountName}\",\"{accountNumber}\",\"Brokerage\",\"{type}\",\"{desc}\",\"{cusip}\",\"{ticker}\",\"{secType}\",\"USD\",\"{price}\",\"{price}\",\"{qty}\",\"\",\"\",\"\",\"\",\"{amount}\",\"{amount}\",\"\",\"\",\"0\",\"\",\"\",\"0\",\"{type}\",\"\",\"0\",\"0\"";

    private static Stream Csv(params string[] rows)
    {
        // header row is UNQUOTED, data rows fully quoted, UTF-8 with a real BOM, CRLF
        var text = Header + "\r\n" + string.Join("\r\n", rows);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        return new MemoryStream(bytes);
    }

    [Fact]
    public void Buy_stock_row_parses_with_total_cost_from_amount()
    {
        var r = ChaseCsvParser.Parse(Csv(Row("7/2/2026", "Buy", "MICRON TECHNOLOGY INC UNSOLICITED", "595112103", "MU", "Stock", "984.28", "0.06235", "-61.37")), ColumnMap.Default);
        var t = Assert.Single(r.Trades);
        Assert.Equal(TxType.Buy, t.Type);
        Assert.Equal(new DateOnly(2026, 7, 2), t.TradeDate);
        Assert.Equal("MU", t.Ticker);
        Assert.Equal("595112103", t.Cusip);
        Assert.Equal("...1234", t.AccountNumber);
        Assert.Equal(0.06235m, t.Shares);
        Assert.Equal(61.37m, t.Amount); // abs(Amount USD), NOT Price*Qty
    }

    [Fact]
    public void Cost_basis_comes_from_amount_because_price_is_rounded_to_2_decimals()
    {
        // real export: Buy 10 SMPL, Price USD "13.05", Amount USD "-130.45" (true fill 13.045)
        var r = ChaseCsvParser.Parse(Csv(Row("6/9/2026", "Buy", "SIMPLY GOOD FOODS", "82900L102", "SMPL", "Stock", "13.05", "10", "-130.45")), ColumnMap.Default);
        var t = Assert.Single(r.Trades);
        Assert.Equal(130.45m, t.Amount);
        Assert.Equal(13.045m, t.Amount / t.Shares);   // effective cost/share, never Price USD
    }

    [Fact]
    public void Sell_amount_is_net_proceeds_after_fees_never_reconstructed_from_price()
    {
        // real export: Sell 12 BSV @ 77.67 gross = 932.04, Amount = 931.96 net
        var r = ChaseCsvParser.Parse(Csv(Row("6/30/2026", "Sell", "VANGUARD SHORT TERM BOND ETF", "921937827", "BSV", "Stock", "77.67", "-12", "931.96")), ColumnMap.Default);
        var t = Assert.Single(r.Trades);
        Assert.Equal(TxType.Sell, t.Type);
        Assert.Equal(12m, t.Shares);
        Assert.Equal(931.96m, t.Amount);              // the tax-correct number
    }

    [Fact]
    public void Split_whole_plus_fractional_sell_rows_stay_separate()
    {
        // one liquidation split by Chase into whole + fractional rows: same date,
        // same price — the dedupe key distinguishes them by Quantity
        var r = ChaseCsvParser.Parse(Csv(
            Row("6/30/2026", "Sell", "VANGUARD SHORT TERM BOND ETF", "921937827", "BSV", "Stock", "77.67", "-12", "931.96"),
            Row("6/30/2026", "Sell", "VANGUARD SHORT TERM BOND ETF", "921937827", "BSV", "Stock", "77.67", "-0.8164", "63.40")), ColumnMap.Default);
        Assert.Equal(2, r.Trades.Count);
        Assert.Equal(2, r.Trades.Select(t => t.DedupeKey).Distinct().Count());
    }

    [Fact]
    public void Sweep_rows_are_skipped_deposits_and_dividends_become_cash_flows()
    {
        var r = ChaseCsvParser.Parse(Csv(
            Row("7/6/2026", "Dividend", "VANGUARD CASH DIV", "921937827", "BSV", "Stock", "0", "0", "3.35"),
            Row("7/6/2026", "WDL", "CHASE DEPOSIT SWEEP", "", "QACDS", "Money Market", "0", "-814.65", "814.65"),
            Row("7/6/2026", "BNK", "BANKLINK ACH PULL", "", "", "Other", "0", "0", "62"),
            Row("7/6/2026", "DBS", "SWEEP DEPOSIT", "", "QACDS", "Money Market", "0", "100", "-100")), ColumnMap.Default);
        Assert.Empty(r.Trades);
        Assert.Equal(2, r.SkippedKnown); // WDL + DBS sweep moves
        Assert.Empty(r.UnrecognizedTypes);

        Assert.Equal(2, r.CashFlows.Count);
        var div = r.CashFlows.Single(c => c.Type == CashFlowType.Dividend);
        Assert.Equal("BSV", div.Ticker);
        Assert.Equal(3.35m, div.Amount);
        var dep = r.CashFlows.Single(c => c.Type == CashFlowType.Deposit);
        Assert.Null(dep.Ticker);
        Assert.Equal(62m, dep.Amount);
        Assert.Equal("...1234", dep.AccountNumber);
        Assert.Equal(new DateOnly(2026, 7, 6), dep.TradeDate);
    }

    [Fact]
    public void Ira_sweep_ticker_QDERQ_is_skipped_by_security_type_not_ticker()
    {
        // sweep tickers vary by account type — recognition is by Security Type
        var r = ChaseCsvParser.Parse(Csv(
            Row("6/26/2026", "WDL", "CHASE IRA DEPOSIT SWEEP JPMORGAN CHASE BANK NA INTRA-DAY WITHDRWAL", "", "QDERQ", "Money Market", "0", "-51.20", "51.20",
                "Self-Directed-Ret", "...5678")), ColumnMap.Default);
        Assert.Empty(r.Trades);
        Assert.Equal(1, r.SkippedKnown);
    }

    [Fact]
    public void Accounts_are_collected_with_ira_evidence()
    {
        var r = ChaseCsvParser.Parse(Csv(
            Row("7/2/2026", "Buy", "MU BUY", "595112103", "MU", "Stock", "984.28", "0.06235", "-61.37"),
            Row("6/12/2026", "Buy", "VOO BUY", "922908363", "VOO", "Stock", "510.00", "0.1", "-51.00",
                "Self-Directed-Ret", "...5678")), ColumnMap.Default);
        Assert.Equal(2, r.Accounts.Count);
        var taxable = r.Accounts["...1234"];
        Assert.False(taxable.SuggestIra);
        Assert.Equal("Self-Directed", taxable.AccountName);
        var ira = r.Accounts["...5678"];
        Assert.True(ira.SuggestIra);                   // "Ret" in Account Name
        Assert.Contains("Ret", ira.IraEvidence);
    }

    [Fact]
    public void Ira_sweep_description_suggests_ira_even_without_ret_in_the_name()
    {
        var r = ChaseCsvParser.Parse(Csv(
            Row("6/26/2026", "WDL", "CHASE IRA DEPOSIT SWEEP JPMORGAN CHASE BANK NA", "", "QDERQ", "Money Market", "0", "-51.20", "51.20",
                "Self-Directed", "...5678")), ColumnMap.Default);
        Assert.True(r.Accounts["...5678"].SuggestIra);
        Assert.Contains("CHASE IRA DEPOSIT SWEEP", r.Accounts["...5678"].IraEvidence);
    }

    [Fact]
    public void Bnk_row_referencing_ira_suggests_ira()
    {
        var r = ChaseCsvParser.Parse(Csv(
            Row("6/12/2026", "BNK", "BANKLINK ACH PULL IRA:C2026RTH 10000003", "", "", "Other", "0", "0", "51",
                "Self-Directed", "...5678")), ColumnMap.Default);
        Assert.True(r.Accounts["...5678"].SuggestIra);
        Assert.Contains("IRA:C2026RTH", r.Accounts["...5678"].IraEvidence);
    }

    [Fact]
    public void Same_day_same_amount_deposits_get_distinct_dedupe_keys()
    {
        var r = ChaseCsvParser.Parse(Csv(
            Row("6/22/2026", "BNK", "BANKLINK ACH PULL 76865450", "", "", "Other", "0", "0", "100"),
            Row("6/22/2026", "BNK", "BANKLINK ACH PULL 76859902", "", "", "Other", "0", "0", "100")), ColumnMap.Default);
        Assert.Equal(2, r.CashFlows.Count);
        Assert.Equal(2, r.CashFlows.Select(c => c.DedupeKey).Distinct().Count());
    }

    [Fact]
    public void Money_market_dividend_stays_skipped()
    {
        var r = ChaseCsvParser.Parse(Csv(
            Row("7/6/2026", "Dividend", "SWEEP INTEREST", "", "QACDS", "Money Market", "0", "0", "0.42")), ColumnMap.Default);
        Assert.Empty(r.CashFlows);
        Assert.Equal(1, r.SkippedKnown);
    }

    [Fact]
    public void Buy_of_money_market_is_skipped_not_imported()
    {
        var r = ChaseCsvParser.Parse(Csv(Row("7/6/2026", "Buy", "SWEEP", "", "QACDS", "Money Market", "1", "814.65", "-814.65")), ColumnMap.Default);
        Assert.Empty(r.Trades);
        Assert.Equal(1, r.SkippedKnown);
    }

    [Fact]
    public void Unrecognized_type_is_reported()
    {
        var r = ChaseCsvParser.Parse(Csv(Row("7/6/2026", "Journal", "XFER", "", "ABC", "Stock", "1", "1", "-1")), ColumnMap.Default);
        Assert.Empty(r.Trades);
        Assert.Equal(1, r.UnrecognizedTypes["Journal"]);
    }

    [Fact]
    public void DedupeKey_routes_by_account_and_keeps_legacy_form()
    {
        var r = ChaseCsvParser.Parse(Csv(Row("7/2/2026", "Sell", "X", "", "RCAT", "Stock", "10.72", "-10", "107.14")), ColumnMap.Default);
        var t = r.Trades.Single();
        Assert.Equal("...1234|7/2/2026|Sell|RCAT|-10|107.14", t.DedupeKey);
        Assert.Equal("7/2/2026|Sell|RCAT|-10|107.14", t.LegacyDedupeKey);  // pre-account rows match on this
    }

    [Fact]
    public void Same_trade_in_two_accounts_gets_distinct_dedupe_keys()
    {
        var r = ChaseCsvParser.Parse(Csv(
            Row("6/12/2026", "Buy", "VOO BUY", "922908363", "VOO", "Stock", "510.00", "0.1", "-51.00"),
            Row("6/12/2026", "Buy", "VOO BUY", "922908363", "VOO", "Stock", "510.00", "0.1", "-51.00",
                "Self-Directed-Ret", "...5678")), ColumnMap.Default);
        Assert.Equal(2, r.Trades.Select(t => t.DedupeKey).Distinct().Count());
    }

    [Fact]
    public void ValidateHeaders_returns_null_for_exact_chase_header()
    {
        Assert.Null(ChaseCsvParser.ValidateHeaders(Csv()));
    }

    [Fact]
    public void ValidateHeaders_returns_actual_headers_on_mismatch()
    {
        var bytes = Encoding.UTF8.GetBytes("Date,Kind,Symbol\r\n\"7/2/2026\",\"Buy\",\"MU\"");
        var actual = ChaseCsvParser.ValidateHeaders(new MemoryStream(bytes));
        Assert.Equal(new[] { "Date", "Kind", "Symbol" }, actual);
    }

    [Fact]
    public void Remapped_columns_parse_via_custom_map()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "Date,Acct,Acct#,Kind,Note,CUSIP#,Symbol,Class,Px,Qty,Amt\r\n" +
            "\"7/2/2026\",\"Self-Directed\",\"...1234\",\"Buy\",\"MU BUY\",\"595112103\",\"MU\",\"Stock\",\"984.28\",\"0.06235\",\"-61.37\"");
        var map = new ColumnMap(new Dictionary<string, string> {
            ["Trade Date"] = "Date", ["Account Name"] = "Acct", ["Account Number"] = "Acct#",
            ["Type"] = "Kind", ["Description"] = "Note", ["Cusip"] = "CUSIP#",
            ["Ticker"] = "Symbol", ["Security Type"] = "Class", ["Price USD"] = "Px",
            ["Quantity"] = "Qty", ["Amount USD"] = "Amt" });
        var r = ChaseCsvParser.Parse(new MemoryStream(bytes), map);
        Assert.Equal(0.06235m, r.Trades.Single().Shares);
        Assert.Equal("...1234", r.Trades.Single().AccountNumber);
    }
}
