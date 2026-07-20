using PortfolioTracker.Core.Sectors;

namespace PortfolioTracker.Core.Data;

/// One freshness stamp per (ticker, data kind) — the crawler's only memory.
/// Because the work plan is recomputed from these on every pass, an
/// interrupted crawl resumes for free after a restart: stamped items fall out
/// of the plan, unstamped ones come back.
public class CrawlStampRepository(Database db)
{
    public void Stamp(string ticker, CrawlKind kind, DateTimeOffset at)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CrawlStamp (Ticker, Kind, FetchedAt) VALUES (@t, @k, @f)
            ON CONFLICT(Ticker, Kind) DO UPDATE SET FetchedAt=excluded.FetchedAt
            """;
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@k", (int)kind);
        cmd.Parameters.AddWithValue("@f", Sql.Ts(at));
        cmd.ExecuteNonQuery();
    }

    public Dictionary<(string Ticker, CrawlKind Kind), DateTimeOffset> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker, Kind, FetchedAt FROM CrawlStamp";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<(string, CrawlKind), DateTimeOffset>();
        while (r.Read())
            map[(r.GetString(0), (CrawlKind)r.GetInt32(1))] = Sql.ToTs(r.GetString(2));
        return map;
    }
}
