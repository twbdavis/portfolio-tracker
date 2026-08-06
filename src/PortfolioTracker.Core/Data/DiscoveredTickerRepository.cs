using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class DiscoveredTickerRepository(Database db)
{
    /// First discovery wins: re-discovering an existing row (any status,
    /// including Ignored) is a no-op, so ignored tickers never resurface.
    public bool InsertIfNew(string ticker, string sourceTicker, DateTimeOffset at)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO DiscoveredTicker (Ticker, SourceTicker, DiscoveredAt, Status)
            VALUES (@t, @s, @a, 0)
            """;
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@s", sourceTicker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@a", Sql.Ts(at));
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<DiscoveredTicker> GetAll(DiscoveryStatus? status = null)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = status is null
            ? "SELECT Ticker, SourceTicker, DiscoveredAt, Status FROM DiscoveredTicker ORDER BY DiscoveredAt DESC"
            : "SELECT Ticker, SourceTicker, DiscoveredAt, Status FROM DiscoveredTicker WHERE Status=@s ORDER BY DiscoveredAt DESC";
        if (status is DiscoveryStatus s) cmd.Parameters.AddWithValue("@s", (int)s);
        using var r = cmd.ExecuteReader();
        var list = new List<DiscoveredTicker>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public DiscoveredTicker? Get(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker, SourceTicker, DiscoveredAt, Status FROM DiscoveredTicker WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public HashSet<string> GetAllTickers()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker FROM DiscoveredTicker";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public void SetStatus(string ticker, DiscoveryStatus status)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE DiscoveredTicker SET Status=@s WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@s", (int)status);
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.ExecuteNonQuery();
    }

    private static DiscoveredTicker Map(SqliteDataReader r) => new()
    {
        Ticker = r.GetString(0),
        SourceTicker = r.GetString(1),
        DiscoveredAt = Sql.ToTs(r.GetString(2)),
        Status = (DiscoveryStatus)r.GetInt32(3),
    };
}
