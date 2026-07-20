using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Core.Import;

public record TickerReconciliation(long AccountId, string Account, string Ticker, decimal Shares, bool NeedsOpeningBalance);

/// An account number the file mentions that the database has never seen,
/// with the auto-suggested treatment for the new-account dialog.
public class NewAccountSuggestion
{
    public required string AccountNumber { get; init; }
    public string? AccountNameRaw { get; init; }
    public string SuggestedName => string.IsNullOrEmpty(AccountNameRaw)
        ? AccountNumber : $"{AccountNameRaw} {AccountNumber}";
    public TaxTreatment SuggestedTreatment { get; init; }
    public string? Evidence { get; init; }
}

/// The decision the user (or a default) makes for one unknown account.
public record NewAccountDecision(string DisplayName, TaxTreatment Treatment);

/// Parse-only result: nothing is committed until every account is resolved.
public class ImportPreview
{
    public required ChaseParseResult Parsed { get; init; }
    public List<NewAccountSuggestion> UnknownAccounts { get; } = new();
    /// Account number the migration placeholder account will adopt on commit
    /// (its rows overlap this file's), or null.
    public string? AdoptedByPlaceholder { get; set; }
}

public class ImportResult
{
    public List<long> BatchIds { get; set; } = new();
    public int RowsImported { get; set; }
    public int RowsSkipped { get; set; }
    public int RowsDeduped { get; set; }
    public Dictionary<string, int> UnrecognizedTypes { get; set; } = new();
    public List<TickerReconciliation> Reconciliation { get; set; } = new();
}

public class ImportService
{
    private readonly AccountRepository _accounts;
    private readonly TransactionRepository _txs;
    private readonly CashFlowRepository _cash;
    private readonly ImportBatchRepository _batches;
    private readonly Rebuilder _rebuilder;

    public ImportService(Database db)
    {
        _accounts = new AccountRepository(db);
        _txs = new TransactionRepository(db);
        _cash = new CashFlowRepository(db);
        _batches = new ImportBatchRepository(db);
        _rebuilder = new Rebuilder(_accounts, new PositionRepository(db), _txs,
            new LotRepository(db), new SaleRepository(db),
            new LotConsumptionRepository(db), new WashSaleAdjustmentRepository(db));
    }

    /// Phase 1: parse and find account numbers that need a new-account decision.
    /// Runs before any write so the user can cancel cleanly.
    public ImportPreview Preview(Stream stream, ColumnMap map)
    {
        var parsed = ChaseCsvParser.Parse(stream, map);
        var preview = new ImportPreview { Parsed = parsed };
        var known = _accounts.GetAll();
        var placeholder = known.FirstOrDefault(a => a.AccountNumber.Length == 0);

        var unknown = parsed.Accounts.Values
            .Where(i => i.AccountNumber.Length > 0
                        && !known.Any(a => a.AccountNumber.Length > 0
                                           && AccountNumbers.Match(a.AccountNumber, i.AccountNumber)))
            .ToList();

        // Pre-migration rows couldn't store their account number; if this file's
        // rows overlap them, that unknown number IS the placeholder account.
        if (placeholder is not null && unknown.Count > 0)
        {
            var seen = _txs.GetAllDedupeKeys();
            seen.UnionWith(_cash.GetAllDedupeKeys());
            var overlap = unknown.ToDictionary(i => i.AccountNumber, i =>
                parsed.Trades.Where(t => t.AccountNumber == i.AccountNumber).Count(t => seen.Contains(t.LegacyDedupeKey))
                + parsed.CashFlows.Where(c => c.AccountNumber == i.AccountNumber).Count(c => seen.Contains(c.LegacyDedupeKey)));
            var adopted = overlap.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key).FirstOrDefault();
            if (adopted is not null)
            {
                preview.AdoptedByPlaceholder = adopted;
                unknown.RemoveAll(i => i.AccountNumber == adopted);
            }
        }

        foreach (var info in unknown)
        {
            preview.UnknownAccounts.Add(new NewAccountSuggestion
            {
                AccountNumber = info.AccountNumber,
                AccountNameRaw = info.AccountName,
                SuggestedTreatment = info.SuggestIra ? TaxTreatment.RothIRA : TaxTreatment.Taxable,
                Evidence = info.IraEvidence,
            });
        }
        return preview;
    }

    /// Phase 2: route rows account by account, one batch per account, then
    /// rebuild each touched (account, ticker). Every account number in the file
    /// must exist by now — Preview + new-account decisions guarantee that.
    public ImportResult Commit(ImportPreview preview, string fileName)
    {
        var parsed = preview.Parsed;
        ApplyAdoption(preview, parsed);
        // masked ("...1234") and unmasked ("1234") forms of one number must hit
        // the same account, so the routing key is the normalized number
        var byNumber = _accounts.GetAll()
            .Where(a => a.AccountNumber.Length > 0)
            .GroupBy(a => AccountNumbers.Normalize(a.AccountNumber))
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var number in parsed.Accounts.Keys.Where(n => n.Length > 0 && !byNumber.ContainsKey(AccountNumbers.Normalize(n))))
            throw new InvalidOperationException($"Account {number} was not created before commit.");

        // one pass over dedupe keys; a row matches either the account-scoped key
        // or the pre-migration key of the same row
        var seen = _txs.GetAllDedupeKeys();
        seen.UnionWith(_cash.GetAllDedupeKeys());
        var fresh = new List<ParsedTradeRow>();
        var freshCash = new List<ParsedCashRow>();
        var dedupedByAccount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void CountDup(string account) =>
            dedupedByAccount[account] = dedupedByAccount.GetValueOrDefault(account) + 1;
        foreach (var row in parsed.Trades)
        {
            if (!seen.Contains(row.DedupeKey) && !seen.Contains(row.LegacyDedupeKey))
            { seen.Add(row.DedupeKey); fresh.Add(row); }
            else CountDup(row.AccountNumber);
        }
        foreach (var row in parsed.CashFlows)
        {
            if (!seen.Contains(row.DedupeKey) && !seen.Contains(row.LegacyDedupeKey))
            { seen.Add(row.DedupeKey); freshCash.Add(row); }
            else CountDup(row.AccountNumber);
        }

        var result = new ImportResult
        {
            RowsImported = fresh.Count + freshCash.Count,
            RowsSkipped = parsed.SkippedKnown + parsed.UnrecognizedTypes.Values.Sum(),
            RowsDeduped = dedupedByAccount.Values.Sum(),
            UnrecognizedTypes = parsed.UnrecognizedTypes,
        };

        var touched = new List<(Account Account, string Ticker)>();
        foreach (var number in parsed.Accounts.Keys.Where(n => n.Length > 0).OrderBy(n => n))
        {
            var account = byNumber[AccountNumbers.Normalize(number)];
            var trades = fresh.Where(r => string.Equals(r.AccountNumber, number, StringComparison.OrdinalIgnoreCase)).ToList();
            var cash = freshCash.Where(r => string.Equals(r.AccountNumber, number, StringComparison.OrdinalIgnoreCase)).ToList();
            if (trades.Count == 0 && cash.Count == 0 && dedupedByAccount.GetValueOrDefault(number) == 0)
                continue;

            var batchId = _batches.Insert(new ImportBatch
            {
                AccountId = account.Id, ImportedAt = DateTimeOffset.Now, FileName = fileName,
                RowsImported = trades.Count + cash.Count,
                RowsSkipped = parsed.Accounts[number].SkippedKnown,
                RowsDeduped = dedupedByAccount.GetValueOrDefault(number),
            });
            result.BatchIds.Add(batchId);

            foreach (var row in trades)
            {
                _txs.Insert(new Transaction
                {
                    AccountId = account.Id, Ticker = row.Ticker, Cusip = row.Cusip,
                    TradeDate = row.TradeDate, Type = row.Type, Shares = row.Shares,
                    Amount = row.Amount, Description = row.Description,
                    Source = LotSource.ChaseImport, ImportBatchId = batchId, DedupeKey = row.DedupeKey,
                });
            }
            foreach (var row in cash)
            {
                _cash.Insert(new CashFlow
                {
                    AccountId = account.Id, TradeDate = row.TradeDate, Type = row.Type,
                    Ticker = row.Ticker, Amount = row.Amount, Description = row.Description,
                    ImportBatchId = batchId, DedupeKey = row.DedupeKey,
                });
            }
            touched.AddRange(trades.Select(r => r.Ticker)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t)
                .Select(t => (account, t)));
        }

        foreach (var (account, ticker) in touched)
        {
            var outcome = _rebuilder.RebuildTicker(account.Id, ticker);
            result.Reconciliation.Add(new TickerReconciliation(
                account.Id, account.DisplayName, ticker, outcome.NetShares, outcome.NeedsOpeningBalance));
        }
        return result;
    }

    /// One call for tests and headless use: unknown accounts are created via the
    /// callback, or with their auto-suggested name/treatment when none is given.
    /// Returns null when the callback declines an account (user cancelled).
    public ImportResult? Import(Stream stream, string fileName, ColumnMap map,
        Func<NewAccountSuggestion, NewAccountDecision?>? onNewAccount = null)
    {
        var preview = Preview(stream, map);
        foreach (var suggestion in preview.UnknownAccounts)
        {
            var decision = onNewAccount is null
                ? new NewAccountDecision(suggestion.SuggestedName, suggestion.SuggestedTreatment)
                : onNewAccount(suggestion);
            if (decision is null) return null;
            CreateAccount(suggestion, decision);
        }
        return Commit(preview, fileName);
    }

    public Account CreateAccount(NewAccountSuggestion suggestion, NewAccountDecision decision)
    {
        var account = new Account
        {
            AccountNumber = suggestion.AccountNumber,
            AccountNameRaw = suggestion.AccountNameRaw,
            DisplayName = decision.DisplayName,
            TaxTreatment = decision.Treatment,
            CreatedAt = DateTimeOffset.Now,
            SortOrder = _accounts.GetAll().Count,
        };
        _accounts.Insert(account);
        return account;
    }

    private void ApplyAdoption(ImportPreview preview, ChaseParseResult parsed)
    {
        if (preview.AdoptedByPlaceholder is not string number) return;
        var placeholder = _accounts.GetAll().FirstOrDefault(a => a.AccountNumber.Length == 0);
        if (placeholder is null) return; // already adopted (e.g. commit retried)
        placeholder.AccountNumber = number;
        placeholder.AccountNameRaw ??= parsed.Accounts.TryGetValue(number, out var info) ? info.AccountName : null;
        _accounts.Update(placeholder);
    }

    public void DeleteBatch(long batchId)
    {
        var batch = _batches.GetAll().FirstOrDefault(b => b.Id == batchId);
        var tickers = _txs.GetTickersForBatch(batchId);
        _txs.DeleteBatch(batchId);
        _cash.DeleteBatch(batchId);
        _batches.Delete(batchId);
        if (batch is null) return;
        foreach (var t in tickers) _rebuilder.RebuildTicker(batch.AccountId, t);
    }
}
