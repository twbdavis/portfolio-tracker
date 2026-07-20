using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class WashSaleAdjustmentRepository(Database db)
{
    public void ReplaceForPosition(long positionId, List<WashSaleAdjustment> rows)
    {
        using var conn = db.Open();
        using var txn = conn.BeginTransaction();
        var del = conn.CreateCommand();
        del.Transaction = txn;
        del.CommandText = "DELETE FROM WashSaleAdjustment WHERE PositionId=@p";
        del.Parameters.AddWithValue("@p", positionId);
        del.ExecuteNonQuery();
        foreach (var w in rows)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
                INSERT INTO WashSaleAdjustment (PositionId, LotConsumptionId, ReplacementLotId,
                    SharesMatched, DisallowedLoss, Kind, CreatedAt)
                VALUES (@p, @c, @l, @s, @d, @k, @at)
                """;
            cmd.Parameters.AddWithValue("@p", positionId);
            cmd.Parameters.AddWithValue("@c", w.LotConsumptionId);
            cmd.Parameters.AddWithValue("@l", w.ReplacementLotId);
            cmd.Parameters.AddWithValue("@s", Sql.Dec(w.SharesMatched));
            cmd.Parameters.AddWithValue("@d", Sql.Dec(w.DisallowedLoss));
            cmd.Parameters.AddWithValue("@k", (int)w.Kind);
            cmd.Parameters.AddWithValue("@at", Sql.Ts(w.CreatedAt));
            cmd.ExecuteNonQuery();
        }
        txn.Commit();
    }

    public List<WashSaleAdjustment> GetByPosition(long positionId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM WashSaleAdjustment WHERE PositionId=@p ORDER BY Id";
        cmd.Parameters.AddWithValue("@p", positionId);
        return ReadAll(cmd);
    }

    public List<WashSaleAdjustment> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM WashSaleAdjustment ORDER BY Id";
        return ReadAll(cmd);
    }

    private static List<WashSaleAdjustment> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<WashSaleAdjustment>();
        while (r.Read()) list.Add(new WashSaleAdjustment
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            PositionId = r.GetInt64(r.GetOrdinal("PositionId")),
            LotConsumptionId = r.GetInt64(r.GetOrdinal("LotConsumptionId")),
            ReplacementLotId = r.GetInt64(r.GetOrdinal("ReplacementLotId")),
            SharesMatched = Sql.ToDec(r.GetString(r.GetOrdinal("SharesMatched"))),
            DisallowedLoss = Sql.ToDec(r.GetString(r.GetOrdinal("DisallowedLoss"))),
            Kind = (WashKind)r.GetInt32(r.GetOrdinal("Kind")),
            CreatedAt = Sql.ToTs(r.GetString(r.GetOrdinal("CreatedAt"))),
        });
        return list;
    }
}
