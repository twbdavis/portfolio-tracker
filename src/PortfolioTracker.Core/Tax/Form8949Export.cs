using System.Globalization;
using System.Text;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tax;

/// Form 8949-style CSV for reconciling against the broker's 1099-B: short-term
/// section then long-term, wash sales flagged with adjustment code W and the
/// disallowed amount, gain/loss reported wash-adjusted. Taxable accounts only;
/// each row names its account. Cross-account and IRA washes will NOT appear on
/// any single 1099-B — they exist only because this app sees every account.
public static class Form8949Export
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string BuildCsv(int year, List<TaxYearEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Form 8949-style export,Tax year {year},ESTIMATES ONLY - reconcile against your broker's 1099-B");
        WriteSection(sb, "SHORT-TERM", events.Where(e => e.View.EffectiveTerm == HoldingTerm.ShortTerm));
        sb.AppendLine();
        WriteSection(sb, "LONG-TERM", events.Where(e => e.View.EffectiveTerm == HoldingTerm.LongTerm));
        return sb.ToString();
    }

    private static void WriteSection(StringBuilder sb, string title, IEnumerable<TaxYearEvent> events)
    {
        sb.AppendLine(title);
        sb.AppendLine("Description,Account,Date Acquired,Date Sold,Proceeds,Cost Basis,Adjustment Code,Adjustment Amount,Gain/Loss");
        decimal proceeds = 0m, basis = 0m, adjustment = 0m, gainLoss = 0m;
        foreach (var e in events.OrderBy(x => x.View.Source.SaleTradeDate).ThenBy(x => x.Ticker))
        {
            var v = e.View;
            var c = v.Source;
            var code = v.IsWash ? "W" : "";
            var adj = v.IsWash ? v.DisallowedLoss : 0m;
            sb.AppendLine(string.Join(',',
                Csv($"{c.SharesConsumed.ToString("0.#####", Inv)} sh {e.Ticker}"),
                Csv(e.Account),
                c.LotAcquisitionDate.ToString("M/d/yyyy", Inv),
                c.SaleTradeDate.ToString("M/d/yyyy", Inv),
                Dec(c.Proceeds),
                Dec(v.AdjustedBasis),
                code,
                v.IsWash ? Dec(adj) : "",
                Dec(v.AdjustedGainLoss)));
            proceeds += c.Proceeds; basis += v.AdjustedBasis; adjustment += adj; gainLoss += v.AdjustedGainLoss;
        }
        sb.AppendLine($"TOTAL,,,,{Dec(proceeds)},{Dec(basis)},,{Dec(adjustment)},{Dec(gainLoss)}");
    }

    private static string Dec(decimal d) => d.ToString("0.00###", Inv);

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
