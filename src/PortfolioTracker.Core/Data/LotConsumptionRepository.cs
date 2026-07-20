using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class LotConsumptionRepository(Database db)
{
    public void ReplaceForPosition(long positionId, List<LotConsumption> rows)
    {
        using var conn = db.Open();
        using var txn = conn.BeginTransaction();
        var del = conn.CreateCommand();
        del.Transaction = txn;
        del.CommandText = "DELETE FROM LotConsumption WHERE PositionId=@p";
        del.Parameters.AddWithValue("@p", positionId);
        del.ExecuteNonQuery();
        foreach (var c in rows)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
                INSERT INTO LotConsumption (PositionId, SaleRecordId, LotId, SharesConsumed,
                    CostBasisConsumed, Proceeds, LotAcquisitionDate, SaleTradeDate,
                    HoldingDays, Term, GainLoss)
                VALUES (@p, @sr, @l, @sh, @cb, @pr, @ad, @sd, @hd, @t, @gl)
                """;
            cmd.Parameters.AddWithValue("@p", positionId);
            cmd.Parameters.AddWithValue("@sr", c.SaleRecordId);
            cmd.Parameters.AddWithValue("@l", c.LotId);
            cmd.Parameters.AddWithValue("@sh", Sql.Dec(c.SharesConsumed));
            cmd.Parameters.AddWithValue("@cb", Sql.Dec(c.CostBasisConsumed));
            cmd.Parameters.AddWithValue("@pr", Sql.Dec(c.Proceeds));
            cmd.Parameters.AddWithValue("@ad", Sql.Date(c.LotAcquisitionDate));
            cmd.Parameters.AddWithValue("@sd", Sql.Date(c.SaleTradeDate));
            cmd.Parameters.AddWithValue("@hd", c.HoldingDays);
            cmd.Parameters.AddWithValue("@t", (int)c.Term);
            cmd.Parameters.AddWithValue("@gl", Sql.Dec(c.GainLoss));
            cmd.ExecuteNonQuery();
        }
        txn.Commit();
    }

    public List<LotConsumption> GetByPosition(long positionId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM LotConsumption WHERE PositionId=@p ORDER BY SaleTradeDate, SaleRecordId, Id";
        cmd.Parameters.AddWithValue("@p", positionId);
        return ReadAll(cmd);
    }

    public List<LotConsumption> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM LotConsumption ORDER BY SaleTradeDate, SaleRecordId, Id";
        return ReadAll(cmd);
    }

    public bool Any()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM LotConsumption)";
        return (long)cmd.ExecuteScalar()! == 1L;
    }

    private static List<LotConsumption> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<LotConsumption>();
        while (r.Read()) list.Add(new LotConsumption
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            PositionId = r.GetInt64(r.GetOrdinal("PositionId")),
            SaleRecordId = r.GetInt64(r.GetOrdinal("SaleRecordId")),
            LotId = r.GetInt64(r.GetOrdinal("LotId")),
            SharesConsumed = Sql.ToDec(r.GetString(r.GetOrdinal("SharesConsumed"))),
            CostBasisConsumed = Sql.ToDec(r.GetString(r.GetOrdinal("CostBasisConsumed"))),
            Proceeds = Sql.ToDec(r.GetString(r.GetOrdinal("Proceeds"))),
            LotAcquisitionDate = Sql.ToDate(r.GetString(r.GetOrdinal("LotAcquisitionDate"))),
            SaleTradeDate = Sql.ToDate(r.GetString(r.GetOrdinal("SaleTradeDate"))),
            HoldingDays = r.GetInt32(r.GetOrdinal("HoldingDays")),
            Term = (HoldingTerm)r.GetInt32(r.GetOrdinal("Term")),
            GainLoss = Sql.ToDec(r.GetString(r.GetOrdinal("GainLoss"))),
        });
        return list;
    }
}
