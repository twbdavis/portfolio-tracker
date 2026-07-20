using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class RecommendationSnapshotRepository(Database db)
{
    public void UpsertMany(string ticker, IEnumerable<RecommendationSnapshotRow> rows)
    {
        using var conn = db.Open();
        using var txn = conn.BeginTransaction();
        foreach (var s in rows)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
                INSERT INTO RecommendationSnapshot (Ticker, Month, StrongBuy, Buy, Hold, Sell, StrongSell)
                VALUES (@t, @m, @sb, @b, @h, @s, @ss)
                ON CONFLICT(Ticker, Month) DO UPDATE SET
                    StrongBuy=excluded.StrongBuy, Buy=excluded.Buy, Hold=excluded.Hold,
                    Sell=excluded.Sell, StrongSell=excluded.StrongSell
                """;
            cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
            cmd.Parameters.AddWithValue("@m", s.Month);
            cmd.Parameters.AddWithValue("@sb", s.StrongBuy);
            cmd.Parameters.AddWithValue("@b", s.Buy);
            cmd.Parameters.AddWithValue("@h", s.Hold);
            cmd.Parameters.AddWithValue("@s", s.Sell);
            cmd.Parameters.AddWithValue("@ss", s.StrongSell);
            cmd.ExecuteNonQuery();
        }
        txn.Commit();
    }

    /// Newest-first snapshots for one ticker.
    public List<RecommendationSnapshotRow> GetForTicker(string ticker, int months)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Ticker, Month, StrongBuy, Buy, Hold, Sell, StrongSell
            FROM RecommendationSnapshot WHERE Ticker=@t ORDER BY Month DESC LIMIT @n
            """;
        cmd.Parameters.AddWithValue("@t", ticker);
        cmd.Parameters.AddWithValue("@n", months);
        using var r = cmd.ExecuteReader();
        var list = new List<RecommendationSnapshotRow>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// Newest-first snapshots for every ticker, keyed by ticker.
    public Dictionary<string, List<RecommendationSnapshotRow>> GetAllByTicker()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Ticker, Month, StrongBuy, Buy, Hold, Sell, StrongSell
            FROM RecommendationSnapshot ORDER BY Ticker, Month DESC
            """;
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<string, List<RecommendationSnapshotRow>>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            var row = Map(r);
            if (!result.TryGetValue(row.Ticker, out var list))
                result[row.Ticker] = list = new List<RecommendationSnapshotRow>();
            list.Add(row);
        }
        return result;
    }

    private static RecommendationSnapshotRow Map(SqliteDataReader r) => new()
    {
        Ticker = r.GetString(0),
        Month = r.GetString(1),
        StrongBuy = r.GetInt32(2),
        Buy = r.GetInt32(3),
        Hold = r.GetInt32(4),
        Sell = r.GetInt32(5),
        StrongSell = r.GetInt32(6),
    };
}
