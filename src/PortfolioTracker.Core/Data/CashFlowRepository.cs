using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public record CashTotals(decimal Deposits, decimal Dividends);

public class CashFlowRepository(Database db)
{
    public long Insert(CashFlow c)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CashFlow (AccountId, TradeDate, Type, Ticker, Amount, Description, ImportBatchId, DedupeKey)
            VALUES (@acct, @d, @ty, @t, @a, @de, @b, @k);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@acct", c.AccountId);
        cmd.Parameters.AddWithValue("@d", Sql.Date(c.TradeDate));
        cmd.Parameters.AddWithValue("@ty", (int)c.Type);
        cmd.Parameters.AddWithValue("@t", c.Ticker.OrDbNull());
        cmd.Parameters.AddWithValue("@a", Sql.Dec(c.Amount));
        cmd.Parameters.AddWithValue("@de", c.Description.OrDbNull());
        cmd.Parameters.AddWithValue("@b", c.ImportBatchId.OrDbNull());
        cmd.Parameters.AddWithValue("@k", c.DedupeKey.OrDbNull());
        return (long)cmd.ExecuteScalar()!;
    }

    public List<CashFlow> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM CashFlow ORDER BY TradeDate DESC, Id DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<CashFlow>();
        while (r.Read()) list.Add(new CashFlow
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            AccountId = r.GetInt64(r.GetOrdinal("AccountId")),
            TradeDate = Sql.ToDate(r.GetString(r.GetOrdinal("TradeDate"))),
            Type = (CashFlowType)r.GetInt32(r.GetOrdinal("Type")),
            Ticker = r.AsString(r.GetOrdinal("Ticker")),
            Amount = Sql.ToDec(r.GetString(r.GetOrdinal("Amount"))),
            Description = r.AsString(r.GetOrdinal("Description")),
            ImportBatchId = r.AsLong(r.GetOrdinal("ImportBatchId")),
            DedupeKey = r.AsString(r.GetOrdinal("DedupeKey")),
        });
        return list;
    }

    /// Sums in C# because Amount is stored as decimal TEXT. Null = all accounts.
    public CashTotals GetTotals(long? accountId = null)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = accountId is null
            ? "SELECT Type, Amount FROM CashFlow"
            : "SELECT Type, Amount FROM CashFlow WHERE AccountId=@a";
        if (accountId is long a) cmd.Parameters.AddWithValue("@a", a);
        using var r = cmd.ExecuteReader();
        decimal deposits = 0m, dividends = 0m;
        while (r.Read())
        {
            var amount = Sql.ToDec(r.GetString(1));
            if ((CashFlowType)r.GetInt32(0) == CashFlowType.Deposit) deposits += amount;
            else dividends += amount;
        }
        return new CashTotals(deposits, dividends);
    }

    public HashSet<string> GetAllDedupeKeys()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DedupeKey FROM CashFlow WHERE DedupeKey IS NOT NULL";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public void DeleteBatch(long batchId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM CashFlow WHERE ImportBatchId=@b";
        cmd.Parameters.AddWithValue("@b", batchId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteByAccount(long accountId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM CashFlow WHERE AccountId=@a";
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.ExecuteNonQuery();
    }
}
