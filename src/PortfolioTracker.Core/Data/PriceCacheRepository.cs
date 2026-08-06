using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class PriceCacheRepository(Database db)
{
    public void Upsert(PriceQuote q) => UpsertMany(new[] { q });

    /// One connection + transaction for a whole refresh wave.
    public void UpsertMany(IReadOnlyCollection<PriceQuote> quotes)
    {
        if (quotes.Count == 0) return;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO PriceCache (Ticker, LastPrice, FetchedAt, PrevClose, ObservedAt, Source)
            VALUES (@t, @p, @at, @pc, @ob, @src)
            ON CONFLICT(Ticker) DO UPDATE SET LastPrice=excluded.LastPrice, FetchedAt=excluded.FetchedAt,
                PrevClose=excluded.PrevClose, ObservedAt=excluded.ObservedAt, Source=excluded.Source
            """;
        foreach (var q in quotes)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@t", q.Ticker);
            cmd.Parameters.AddWithValue("@p", Sql.Dec(q.Price));
            cmd.Parameters.AddWithValue("@at", Sql.Ts(q.FetchedAt));
            cmd.Parameters.AddWithValue("@pc", q.PrevClose is decimal pc ? Sql.Dec(pc) : DBNull.Value);
            cmd.Parameters.AddWithValue("@ob", q.ObservedAt is DateTimeOffset ob ? Sql.Ts(ob) : DBNull.Value);
            cmd.Parameters.AddWithValue("@src", q.Source is string src ? src : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Delete(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PriceCache WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, PriceQuote> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker, LastPrice, FetchedAt, PrevClose, ObservedAt, Source FROM PriceCache";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            var q = new PriceQuote(r.GetString(0), Sql.ToDec(r.GetString(1)), Sql.ToTs(r.GetString(2)),
                r.IsDBNull(3) ? null : Sql.ToDec(r.GetString(3)),
                r.IsDBNull(4) ? null : Sql.ToTs(r.GetString(4)),
                r.IsDBNull(5) ? null : r.GetString(5));
            map[q.Ticker] = q;
        }
        return map;
    }
}
