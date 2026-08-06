using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Tax;

// `--dir <path>` — run against a copy of the data folder instead of the live
// one (GetYearData upserts carryforward rows, so never point this at the real
// DB while the app is open)
var dirFlag = Array.IndexOf(args, "--dir");
var appData = dirFlag >= 0 && args.Length > dirFlag + 1
    ? args[dirFlag + 1]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PortfolioTracker");
var db = new Database(Path.Combine(appData, "portfolio.db"));

// `db-inspect warehouse` — sector-analysis warehouse coverage counts
if (args.Length > 0 && args[0] == "warehouse")
{
    using var conn = db.Open();
    foreach (var sql in new[]
    {
        "SELECT 'universe tickers', COUNT(DISTINCT Ticker) FROM UniverseMember",
        "SELECT 'classifications', COUNT(*) FROM TickerClassification",
        "SELECT 'metrics rows', COUNT(*) FROM TickerMetrics",
        "SELECT 'recommendation rows', COUNT(*) FROM RecommendationSnapshot",
        "SELECT 'daily closes', COUNT(*) FROM DailyClose",
        "SELECT 'tickers with closes', COUNT(DISTINCT Ticker) FROM DailyClose",
        "SELECT 'stamps kind=' || Kind, COUNT(*) FROM CrawlStamp GROUP BY Kind",
        "SELECT 'signal hits', COUNT(*) FROM SignalHit",
    })
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) Console.WriteLine($"  {r.GetString(0),-24} {r.GetInt64(1)}");
    }
    return;
}

// `db-inspect clear-history-stamps` — one-shot repair: forget History crawl
// stamps (kind=4) and flag the pass incomplete so the crawler re-backfills
// price history on next launch. Used 2026-07-20 after Stooq's bot wall got
// empty results stamped as fetched. Run with the app CLOSED.
if (args.Length > 0 && args[0] == "clear-history-stamps")
{
    using var conn = db.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        DELETE FROM CrawlStamp WHERE Kind = 4;
        INSERT INTO AppState (Key, Value) VALUES ('crawler.passIncomplete', 'true')
        ON CONFLICT(Key) DO UPDATE SET Value='true';
        """;
    Console.WriteLine($"cleared, rows affected: {cmd.ExecuteNonQuery()}");
    return;
}

var tax = new TaxService(db);
var brackets = TaxBracketsFile.Load(Path.Combine(appData, "taxbrackets.json"));

var year = DateTime.Now.Year;
var d = tax.GetYearData(year, brackets);
var economic = d.Events.Sum(e => e.View.Source.GainLoss);
var taxable = d.YearShortTerm + d.YearLongTerm;
Console.WriteLine($"Year {year}");
Console.WriteLine($"  Realized (economic) {economic:F2}");
Console.WriteLine($"  Taxable (adjusted)  {taxable:F2}  (wash add-back {taxable - economic:F2})");
Console.WriteLine($"  Est. attributable   {d.Estimate.Attributable:F2}");
Console.WriteLine($"  After tax (kept)    {economic - d.Estimate.Attributable:F2}");
Console.WriteLine($"  (old GetRealizedAdjusted: {tax.GetRealizedAdjusted(year):F2})");
