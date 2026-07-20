using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Import;

public class ParsedTradeRow
{
    public string AccountNumber { get; init; } = "";
    public DateOnly TradeDate { get; init; }
    public TxType Type { get; init; }
    public string Ticker { get; init; } = "";
    public string? Cusip { get; init; }
    public string? Description { get; init; }
    public decimal Shares { get; init; }   // positive
    public decimal Amount { get; init; }   // Buy: total cost from Amount USD; Sell: NET proceeds. Positive.
    public string DedupeKey { get; init; } = "";
    public string LegacyDedupeKey { get; init; } = "";  // pre-account key format, for pre-migration rows
}

public class ParsedCashRow
{
    public string AccountNumber { get; init; } = "";
    public DateOnly TradeDate { get; init; }
    public CashFlowType Type { get; init; }
    public string? Ticker { get; init; }
    public string? Description { get; init; }
    public decimal Amount { get; init; }   // signed as exported; deposits and dividends are positive
    public string DedupeKey { get; init; } = "";
    public string LegacyDedupeKey { get; init; } = "";
}

/// Everything the file reveals about one account, including the IRA markers
/// used to pre-fill the new-account dialog.
public class ParsedAccountInfo
{
    public string AccountNumber { get; init; } = "";
    public string? AccountName { get; set; }
    public bool SuggestIra { get; set; }
    public string? IraEvidence { get; set; }   // e.g. "Account Name contains 'Ret'" / "IRA:C2026RTH deposit"
    public int SkippedKnown { get; set; }      // sweep/noise rows from this account
}

public class ChaseParseResult
{
    public List<ParsedTradeRow> Trades { get; } = new();
    public List<ParsedCashRow> CashFlows { get; } = new();
    public Dictionary<string, ParsedAccountInfo> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int SkippedKnown { get; set; }
    public Dictionary<string, int> UnrecognizedTypes { get; } = new();
}

public static class ChaseCsvParser
{
    // "Dividend" stays here so non-stock dividends (money market interest) still count as known noise
    private static readonly HashSet<string> SkipTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Dividend", "WDL", "DBS" };
    // Sweep tickers vary by account type (QACDS brokerage, QDERQ IRA) so sweeps
    // are recognized by Security Type, never by ticker.
    private static readonly HashSet<string> SkipSecurityTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Money Market", "Other" };
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    /// Returns null when every default Chase header is present; otherwise the actual headers,
    /// so the UI can offer column remapping instead of crashing.
    public static List<string>? ValidateHeaders(Stream stream)
    {
        using var reader = NewCsvReader(stream);
        if (!reader.Read()) return new List<string>();
        reader.ReadHeader();
        var actual = reader.HeaderRecord ?? Array.Empty<string>();
        var present = new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase);
        return ColumnMap.RequiredFields.All(present.Contains) ? null : actual.ToList();
    }

    public static ChaseParseResult Parse(Stream stream, ColumnMap map)
    {
        var result = new ChaseParseResult();
        using var reader = NewCsvReader(stream);
        reader.Read();
        reader.ReadHeader();
        while (reader.Read())
        {
            string F(string field) => reader.GetField(map.HeaderFor(field))?.Trim() ?? "";
            var typeRaw = F("Type");
            var secType = F("Security Type");
            var ticker = F("Ticker");
            var account = F("Account Number");
            var desc = F("Description");

            var info = AccountInfo(result, account, F("Account Name"));
            NoteIraEvidence(info, typeRaw, desc);

            var isStock = secType.Equals("Stock", StringComparison.OrdinalIgnoreCase);
            var isStockTrade = isStock
                && (typeRaw.Equals("Buy", StringComparison.OrdinalIgnoreCase)
                    || typeRaw.Equals("Sell", StringComparison.OrdinalIgnoreCase));

            if (!isStockTrade)
            {
                var isDeposit = typeRaw.Equals("BNK", StringComparison.OrdinalIgnoreCase);
                var isDividend = isStock && typeRaw.Equals("Dividend", StringComparison.OrdinalIgnoreCase);
                if (isDeposit || isDividend)
                {
                    var cashAmtRaw = F("Amount USD");
                    result.CashFlows.Add(new ParsedCashRow
                    {
                        AccountNumber = account,
                        TradeDate = DateOnly.ParseExact(F("Trade Date"), "M/d/yyyy", EnUs),
                        Type = isDeposit ? CashFlowType.Deposit : CashFlowType.Dividend,
                        Ticker = string.IsNullOrEmpty(ticker) ? null : ticker,
                        Description = desc,
                        Amount = decimal.Parse(cashAmtRaw, NumberStyles.Number, EnUs),
                        // description distinguishes same-day, same-amount deposits (distinct ACH ids)
                        DedupeKey = $"{account}|{F("Trade Date")}|{typeRaw}|{ticker}|{cashAmtRaw}|{desc}",
                        LegacyDedupeKey = $"{F("Trade Date")}|{typeRaw}|{ticker}|{cashAmtRaw}|{desc}",
                    });
                }
                else if (SkipTypes.Contains(typeRaw) || SkipSecurityTypes.Contains(secType))
                { result.SkippedKnown++; info.SkippedKnown++; }
                else
                    result.UnrecognizedTypes[typeRaw] =
                        result.UnrecognizedTypes.GetValueOrDefault(typeRaw) + 1;
                continue;
            }

            var qtyRaw = F("Quantity");
            var amtRaw = F("Amount USD");
            var dateRaw = F("Trade Date");
            var qty = decimal.Parse(qtyRaw, NumberStyles.Number, EnUs);
            var amt = decimal.Parse(amtRaw, NumberStyles.Number, EnUs);
            var isBuy = typeRaw.Equals("Buy", StringComparison.OrdinalIgnoreCase);
            var cusip = F("Cusip");

            // Amount USD is authoritative: buys carry the true total cost (Price
            // USD is rounded to 2 decimals and display-only), sells carry NET
            // proceeds after regulatory fees — the tax-correct number. Never
            // reconstruct either from Price x Quantity.
            result.Trades.Add(new ParsedTradeRow
            {
                AccountNumber = account,
                TradeDate = DateOnly.ParseExact(dateRaw, "M/d/yyyy", EnUs),
                Type = isBuy ? TxType.Buy : TxType.Sell,
                Ticker = ticker,
                Cusip = string.IsNullOrEmpty(cusip) ? null : cusip,
                Description = desc,
                Shares = Math.Abs(qty),
                Amount = Math.Abs(amt),
                // Quantity keeps whole-share and fractional-remainder rows of one
                // liquidation distinct — they are legitimate separate rows.
                DedupeKey = $"{account}|{dateRaw}|{typeRaw}|{ticker}|{qtyRaw}|{amtRaw}",
                LegacyDedupeKey = $"{dateRaw}|{typeRaw}|{ticker}|{qtyRaw}|{amtRaw}",
            });
        }
        return result;
    }

    private static ParsedAccountInfo AccountInfo(ChaseParseResult result, string number, string name)
    {
        if (!result.Accounts.TryGetValue(number, out var info))
        {
            info = new ParsedAccountInfo { AccountNumber = number };
            result.Accounts[number] = info;
        }
        if (string.IsNullOrEmpty(info.AccountName) && !string.IsNullOrEmpty(name))
        {
            info.AccountName = name;
            if (name.Contains("Ret", StringComparison.OrdinalIgnoreCase) && !info.SuggestIra)
            {
                info.SuggestIra = true;
                info.IraEvidence = $"Account Name \"{name}\" contains \"Ret\"";
            }
        }
        return info;
    }

    private static void NoteIraEvidence(ParsedAccountInfo info, string typeRaw, string desc)
    {
        if (info.SuggestIra || string.IsNullOrEmpty(desc)) return;
        if (desc.Contains("CHASE IRA DEPOSIT SWEEP", StringComparison.OrdinalIgnoreCase))
        {
            info.SuggestIra = true;
            info.IraEvidence = "sweep rows use \"CHASE IRA DEPOSIT SWEEP\"";
        }
        else if (typeRaw.Equals("BNK", StringComparison.OrdinalIgnoreCase)
                 && desc.Contains("IRA:", StringComparison.OrdinalIgnoreCase))
        {
            info.SuggestIra = true;
            var at = desc.IndexOf("IRA:", StringComparison.OrdinalIgnoreCase);
            var marker = desc[at..].Split(' ', 2)[0];
            info.IraEvidence = $"deposits reference \"{marker}\"";
        }
    }

    private static CsvReader NewCsvReader(Stream stream)
    {
        // StreamReader with detectEncodingFromByteOrderMarks strips the UTF-8 BOM.
        // Chase writes an UNQUOTED header row over fully-quoted data rows;
        // CsvHelper's defaults handle the mixed quoting.
        var sr = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            BadDataFound = null,
            HeaderValidated = null,
        };
        return new CsvReader(sr, config);
    }
}
