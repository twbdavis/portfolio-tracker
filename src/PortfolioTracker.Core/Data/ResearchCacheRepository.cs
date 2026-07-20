using Microsoft.Data.Sqlite;

namespace PortfolioTracker.Core.Data;

/// Per-(ticker, kind) JSON blobs with TTL enforced on read. Kinds are owned by
/// ResearchService ("profile", "metrics", "recommendations", "earnings",
/// "earningsdate", "news").
public class ResearchCacheRepository(Database db)
{
    /// Returns the cached JSON, or null when absent or older than maxAge.
    public string? Get(string ticker, string kind, TimeSpan maxAge)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FetchedAt, Json FROM ResearchCache WHERE Ticker=@t AND Kind=@k";
        cmd.Parameters.AddWithValue("@t", ticker);
        cmd.Parameters.AddWithValue("@k", kind);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var fetchedAt = Sql.ToTs(r.GetString(0));
        return DateTimeOffset.Now - fetchedAt <= maxAge ? r.GetString(1) : null;
    }

    public void Put(string ticker, string kind, string json, DateTimeOffset fetchedAt)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ResearchCache (Ticker, Kind, FetchedAt, Json) VALUES (@t, @k, @f, @j)
            ON CONFLICT(Ticker, Kind) DO UPDATE SET FetchedAt=@f, Json=@j
            """;
        cmd.Parameters.AddWithValue("@t", ticker);
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@f", Sql.Ts(fetchedAt));
        cmd.Parameters.AddWithValue("@j", json);
        cmd.ExecuteNonQuery();
    }
}
