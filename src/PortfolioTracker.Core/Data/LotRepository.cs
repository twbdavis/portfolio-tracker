using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class LotRepository(Database db)
{
    /// Replaces all lots for a position in one transaction (used by FIFO replay).
    /// ParentLotId on incoming lots refers to the *index* of the parent within
    /// the list (as a long) and is remapped to the real inserted Id; null stays null.
    /// Returns the inserted Ids in input order so callers can map index -> Id.
    public List<long> ReplaceForPosition(long positionId, List<Lot> lots)
    {
        using var conn = db.Open();
        using var txn = conn.BeginTransaction();
        var del = conn.CreateCommand();
        del.Transaction = txn;
        del.CommandText = "DELETE FROM Lot WHERE PositionId=@p";
        del.Parameters.AddWithValue("@p", positionId);
        del.ExecuteNonQuery();

        var insertedIds = new List<long>(lots.Count);
        foreach (var lot in lots)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
                INSERT INTO Lot (AccountId, PositionId, Shares, TotalCost, RemainingShares, RemainingCost,
                                 TradeDate, Source, ImportBatchId, ParentLotId, SourceTxId)
                VALUES (@a, @p, @s, @tc, @rs, @rc, @d, @so, @b, @pl, @tx);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@a", lot.AccountId);
            cmd.Parameters.AddWithValue("@p", positionId);
            cmd.Parameters.AddWithValue("@s", Sql.Dec(lot.Shares));
            cmd.Parameters.AddWithValue("@tc", Sql.Dec(lot.TotalCost));
            cmd.Parameters.AddWithValue("@rs", Sql.Dec(lot.RemainingShares));
            cmd.Parameters.AddWithValue("@rc", Sql.Dec(lot.RemainingCost));
            cmd.Parameters.AddWithValue("@d", Sql.Date(lot.TradeDate));
            cmd.Parameters.AddWithValue("@so", (int)lot.Source);
            cmd.Parameters.AddWithValue("@b", lot.ImportBatchId.OrDbNull());
            cmd.Parameters.AddWithValue("@pl", lot.ParentLotId is long pi && pi >= 0 && pi < insertedIds.Count
                ? insertedIds[(int)pi] : DBNull.Value);
            cmd.Parameters.AddWithValue("@tx", lot.SourceTxId.OrDbNull());
            insertedIds.Add((long)cmd.ExecuteScalar()!);
        }
        txn.Commit();
        return insertedIds;
    }

    public List<Lot> GetByPosition(long positionId, bool openOnly)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Lot WHERE PositionId=@p ORDER BY TradeDate, Id";
        cmd.Parameters.AddWithValue("@p", positionId);
        return ReadAll(cmd, openOnly);
    }

    public List<Lot> GetAllOpen()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Lot ORDER BY PositionId, TradeDate, Id";
        return ReadAll(cmd, openOnly: true);
    }

    /// Every lot, open and consumed — one query for whole-portfolio tax math
    /// instead of a per-position round trip.
    public List<Lot> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Lot ORDER BY PositionId, TradeDate, Id";
        return ReadAll(cmd, openOnly: false);
    }

    private static List<Lot> ReadAll(SqliteCommand cmd, bool openOnly)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Lot>();
        while (r.Read())
        {
            var lot = Map(r);
            if (!openOnly || lot.RemainingShares > 0m) list.Add(lot);
        }
        return list;
    }

    private static Lot Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        AccountId = r.GetInt64(r.GetOrdinal("AccountId")),
        PositionId = r.GetInt64(r.GetOrdinal("PositionId")),
        Shares = Sql.ToDec(r.GetString(r.GetOrdinal("Shares"))),
        TotalCost = Sql.ToDec(r.GetString(r.GetOrdinal("TotalCost"))),
        RemainingShares = Sql.ToDec(r.GetString(r.GetOrdinal("RemainingShares"))),
        RemainingCost = Sql.ToDec(r.GetString(r.GetOrdinal("RemainingCost"))),
        TradeDate = Sql.ToDate(r.GetString(r.GetOrdinal("TradeDate"))),
        Source = (LotSource)r.GetInt32(r.GetOrdinal("Source")),
        ImportBatchId = r.AsLong(r.GetOrdinal("ImportBatchId")),
        ParentLotId = r.AsLong(r.GetOrdinal("ParentLotId")),
        SourceTxId = r.AsLong(r.GetOrdinal("SourceTxId")),
    };
}
