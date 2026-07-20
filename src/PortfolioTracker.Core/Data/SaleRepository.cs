using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class SaleRepository(Database db)
{
    /// Returns the inserted Ids in input order so callers can map index -> Id.
    public List<long> ReplaceForPosition(long positionId, List<SaleRecord> sales)
    {
        using var conn = db.Open();
        using var txn = conn.BeginTransaction();
        var del = conn.CreateCommand();
        del.Transaction = txn;
        del.CommandText = "DELETE FROM SaleRecord WHERE PositionId=@p";
        del.Parameters.AddWithValue("@p", positionId);
        del.ExecuteNonQuery();
        var insertedIds = new List<long>(sales.Count);
        foreach (var s in sales)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
                INSERT INTO SaleRecord (AccountId, PositionId, TradeDate, SharesSold, Proceeds,
                    CostBasisConsumed, RealizedGainLoss, UnmatchedShares, ImportBatchId)
                VALUES (@a, @p, @d, @ss, @pr, @cb, @gl, @us, @b);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@a", s.AccountId);
            cmd.Parameters.AddWithValue("@p", positionId);
            cmd.Parameters.AddWithValue("@d", Sql.Date(s.TradeDate));
            cmd.Parameters.AddWithValue("@ss", Sql.Dec(s.SharesSold));
            cmd.Parameters.AddWithValue("@pr", Sql.Dec(s.Proceeds));
            cmd.Parameters.AddWithValue("@cb", Sql.Dec(s.CostBasisConsumed));
            cmd.Parameters.AddWithValue("@gl", Sql.Dec(s.RealizedGainLoss));
            cmd.Parameters.AddWithValue("@us", Sql.Dec(s.UnmatchedShares));
            cmd.Parameters.AddWithValue("@b", s.ImportBatchId.OrDbNull());
            insertedIds.Add((long)cmd.ExecuteScalar()!);
        }
        txn.Commit();
        return insertedIds;
    }

    public List<SaleRecord> GetByPosition(long positionId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM SaleRecord WHERE PositionId=@p ORDER BY TradeDate, Id";
        cmd.Parameters.AddWithValue("@p", positionId);
        return ReadAll(cmd);
    }

    public List<SaleRecord> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM SaleRecord ORDER BY TradeDate DESC, Id DESC";
        return ReadAll(cmd);
    }

    private static List<SaleRecord> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<SaleRecord>();
        while (r.Read()) list.Add(new SaleRecord
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            AccountId = r.GetInt64(r.GetOrdinal("AccountId")),
            PositionId = r.GetInt64(r.GetOrdinal("PositionId")),
            TradeDate = Sql.ToDate(r.GetString(r.GetOrdinal("TradeDate"))),
            SharesSold = Sql.ToDec(r.GetString(r.GetOrdinal("SharesSold"))),
            Proceeds = Sql.ToDec(r.GetString(r.GetOrdinal("Proceeds"))),
            CostBasisConsumed = Sql.ToDec(r.GetString(r.GetOrdinal("CostBasisConsumed"))),
            RealizedGainLoss = Sql.ToDec(r.GetString(r.GetOrdinal("RealizedGainLoss"))),
            UnmatchedShares = Sql.ToDec(r.GetString(r.GetOrdinal("UnmatchedShares"))),
            ImportBatchId = r.AsLong(r.GetOrdinal("ImportBatchId")),
        });
        return list;
    }
}
