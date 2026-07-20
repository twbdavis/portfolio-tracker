using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class TransactionRepository(Database db)
{
    public long Insert(Transaction t)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Tx (AccountId, Ticker, Cusip, TradeDate, Type, Shares, Amount, Description, Source, ImportBatchId, DedupeKey)
            VALUES (@acct, @t, @c, @d, @ty, @s, @a, @de, @so, @b, @k);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, t);
        return (long)cmd.ExecuteScalar()!;
    }

    public void Update(Transaction t)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Tx SET AccountId=@acct, Ticker=@t, Cusip=@c, TradeDate=@d, Type=@ty, Shares=@s, Amount=@a,
                Description=@de, Source=@so, ImportBatchId=@b, DedupeKey=@k WHERE Id=@id
            """;
        Bind(cmd, t);
        cmd.Parameters.AddWithValue("@id", t.Id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Tx WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public Transaction? GetById(long id)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Tx WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public List<Transaction> GetByTicker(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Tx WHERE Ticker=@t ORDER BY TradeDate, Id";
        cmd.Parameters.AddWithValue("@t", ticker);
        using var r = cmd.ExecuteReader();
        var list = new List<Transaction>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// FIFO input: one account's transactions for one ticker, replay order.
    public List<Transaction> GetByAccountAndTicker(long accountId, string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Tx WHERE AccountId=@a AND Ticker=@t ORDER BY TradeDate, Id";
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@t", ticker);
        using var r = cmd.ExecuteReader();
        var list = new List<Transaction>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public List<string> GetTickersForBatch(long batchId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Ticker FROM Tx WHERE ImportBatchId=@b ORDER BY Ticker";
        cmd.Parameters.AddWithValue("@b", batchId);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void DeleteBatch(long batchId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Tx WHERE ImportBatchId=@b";
        cmd.Parameters.AddWithValue("@b", batchId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteByAccount(long accountId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Tx WHERE AccountId=@a";
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteByAccountAndTicker(long accountId, string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Tx WHERE AccountId=@a AND Ticker=@t";
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@t", ticker);
        cmd.ExecuteNonQuery();
    }

    public HashSet<string> GetAllDedupeKeys()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DedupeKey FROM Tx WHERE DedupeKey IS NOT NULL";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    private static void Bind(SqliteCommand cmd, Transaction t)
    {
        cmd.Parameters.AddWithValue("@acct", t.AccountId);
        cmd.Parameters.AddWithValue("@t", t.Ticker);
        cmd.Parameters.AddWithValue("@c", t.Cusip.OrDbNull());
        cmd.Parameters.AddWithValue("@d", Sql.Date(t.TradeDate));
        cmd.Parameters.AddWithValue("@ty", (int)t.Type);
        cmd.Parameters.AddWithValue("@s", Sql.Dec(t.Shares));
        cmd.Parameters.AddWithValue("@a", Sql.Dec(t.Amount));
        cmd.Parameters.AddWithValue("@de", t.Description.OrDbNull());
        cmd.Parameters.AddWithValue("@so", (int)t.Source);
        cmd.Parameters.AddWithValue("@b", t.ImportBatchId.OrDbNull());
        cmd.Parameters.AddWithValue("@k", t.DedupeKey.OrDbNull());
    }

    private static Transaction Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        AccountId = r.GetInt64(r.GetOrdinal("AccountId")),
        Ticker = r.GetString(r.GetOrdinal("Ticker")),
        Cusip = r.AsString(r.GetOrdinal("Cusip")),
        TradeDate = Sql.ToDate(r.GetString(r.GetOrdinal("TradeDate"))),
        Type = (TxType)r.GetInt32(r.GetOrdinal("Type")),
        Shares = Sql.ToDec(r.GetString(r.GetOrdinal("Shares"))),
        Amount = Sql.ToDec(r.GetString(r.GetOrdinal("Amount"))),
        Description = r.AsString(r.GetOrdinal("Description")),
        Source = (LotSource)r.GetInt32(r.GetOrdinal("Source")),
        ImportBatchId = r.AsLong(r.GetOrdinal("ImportBatchId")),
        DedupeKey = r.AsString(r.GetOrdinal("DedupeKey")),
    };
}
