using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class PortfolioFitRepository(Database db)
{
    public void UpsertMany(IEnumerable<PortfolioFitRow> rows)
    {
        using var conn = db.Open();
        using var txn = conn.BeginTransaction();
        foreach (var row in rows)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
                INSERT INTO PortfolioFit (Ticker, AsOf, Correlation, Observations)
                VALUES (@t, @a, @c, @o)
                ON CONFLICT(Ticker) DO UPDATE SET
                    AsOf=excluded.AsOf, Correlation=excluded.Correlation,
                    Observations=excluded.Observations
                """;
            cmd.Parameters.AddWithValue("@t", row.Ticker.Trim().ToUpperInvariant());
            cmd.Parameters.AddWithValue("@a", Sql.Ts(row.AsOf));
            cmd.Parameters.AddWithValue("@c", row.Correlation is decimal c ? Sql.Dec(c) : DBNull.Value);
            cmd.Parameters.AddWithValue("@o", row.Observations);
            cmd.ExecuteNonQuery();
        }
        txn.Commit();
    }

    public Dictionary<string, PortfolioFitRow> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker, AsOf, Correlation, Observations FROM PortfolioFit";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, PortfolioFitRow>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            var row = new PortfolioFitRow
            {
                Ticker = r.GetString(0),
                AsOf = Sql.ToTs(r.GetString(1)),
                Correlation = r.IsDBNull(2) ? null : Sql.ToDec(r.GetString(2)),
                Observations = r.GetInt32(3),
            };
            map[row.Ticker] = row;
        }
        return map;
    }
}
