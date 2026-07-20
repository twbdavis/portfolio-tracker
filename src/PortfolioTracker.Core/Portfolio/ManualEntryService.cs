using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Portfolio;

public class ManualEntryService
{
    private readonly AccountRepository _accounts;
    private readonly PositionRepository _positions;
    private readonly TransactionRepository _txs;
    private readonly LotRepository _lots;
    private readonly SaleRepository _sales;
    private readonly LotConsumptionRepository _consumptions;
    private readonly WashSaleAdjustmentRepository _washes;
    private readonly TickerTargetRepository _targets;
    private readonly PriceCacheRepository _priceCache;
    private readonly CashFlowRepository _cashFlows;
    private readonly ImportBatchRepository _batches;
    private readonly Rebuilder _rebuilder;

    public ManualEntryService(Database db)
    {
        _accounts = new AccountRepository(db);
        _positions = new PositionRepository(db);
        _txs = new TransactionRepository(db);
        _lots = new LotRepository(db);
        _sales = new SaleRepository(db);
        _consumptions = new LotConsumptionRepository(db);
        _washes = new WashSaleAdjustmentRepository(db);
        _targets = new TickerTargetRepository(db);
        _priceCache = new PriceCacheRepository(db);
        _cashFlows = new CashFlowRepository(db);
        _batches = new ImportBatchRepository(db);
        _rebuilder = new Rebuilder(_accounts, _positions, _txs, _lots, _sales, _consumptions, _washes);
    }

    public void AddLot(long accountId, string ticker, decimal shares, decimal totalCost, DateOnly tradeDate, bool openingBalance)
    {
        if (shares <= 0m) throw new ArgumentOutOfRangeException(nameof(shares), "Shares must be positive.");
        if (totalCost < 0m) throw new ArgumentOutOfRangeException(nameof(totalCost), "Cost cannot be negative.");
        _txs.Insert(new Transaction
        {
            AccountId = accountId, Ticker = ticker.Trim().ToUpperInvariant(),
            TradeDate = tradeDate, Type = TxType.Buy,
            Shares = shares, Amount = totalCost,
            Source = openingBalance ? LotSource.OpeningBalance : LotSource.Manual,
        });
        _rebuilder.RebuildTicker(accountId, ticker.Trim().ToUpperInvariant());
    }

    /// Works for imported lots too: Source and DedupeKey are preserved, so
    /// re-importing the same CSV dedupes against the edited row instead of
    /// duplicating or overwriting it.
    public void UpdateLot(long txId, decimal shares, decimal totalCost, DateOnly tradeDate)
    {
        if (shares <= 0m) throw new ArgumentOutOfRangeException(nameof(shares), "Shares must be positive.");
        var tx = _txs.GetById(txId) ?? throw new InvalidOperationException($"Transaction {txId} not found.");
        tx.Shares = shares; tx.Amount = totalCost; tx.TradeDate = tradeDate;
        _txs.Update(tx);
        _rebuilder.RebuildTicker(tx.AccountId, tx.Ticker);
    }

    /// Deleting an imported lot removes its dedupe key with it, so
    /// re-importing the CSV restores the trade (same as DeletePosition).
    public void DeleteLot(long txId)
    {
        var tx = _txs.GetById(txId) ?? throw new InvalidOperationException($"Transaction {txId} not found.");
        _txs.Delete(txId);
        _rebuilder.RebuildTicker(tx.AccountId, tx.Ticker);
    }

    /// Removes one account's position and all of its data: transactions
    /// (including imported ones — their dedupe keys go with them, so
    /// re-importing the CSV restores them), lots, sales. The ticker's price
    /// cache and target survive while any other account still holds it.
    public void DeletePosition(long positionId)
    {
        var pos = _positions.GetAll().FirstOrDefault(p => p.Id == positionId)
            ?? throw new InvalidOperationException($"Position {positionId} not found.");
        _txs.DeleteByAccountAndTicker(pos.AccountId, pos.Ticker);
        _washes.ReplaceForPosition(pos.Id, []);
        _consumptions.ReplaceForPosition(pos.Id, []);
        _lots.ReplaceForPosition(pos.Id, []);
        _sales.ReplaceForPosition(pos.Id, []);
        _positions.Delete(pos.Id);
        var remaining = _positions.GetAllByTicker(pos.Ticker);
        if (remaining.Count == 0)
        {
            _priceCache.Delete(pos.Ticker);
            _targets.Delete(pos.Ticker);
        }
        else
        {
            // the deleted account's lots may have been wash replacements
            _rebuilder.RecomputeWashes(pos.Ticker);
        }
    }

    /// Removes an account and everything in it: positions (with their lots,
    /// sales, consumptions, wash adjustments, per-ticker cleanup), remaining
    /// transactions, cash flows, and import batches. Wash adjustments for
    /// tickers still held elsewhere are recomputed by the position deletes.
    public void DeleteAccount(long accountId)
    {
        var account = _accounts.GetById(accountId)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");
        foreach (var pos in _positions.GetByAccount(accountId))
            DeletePosition(pos.Id);
        _txs.DeleteByAccount(accountId);      // stray rows without a position yet
        _cashFlows.DeleteByAccount(accountId);
        _batches.DeleteByAccount(accountId);
        _accounts.Delete(account.Id);
    }
}
