using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class SignalHitRepository(Database db)
{
    public long Insert(SignalHit hit)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SignalHit (Ticker, SignalKind, FiredAt, InputsJson, Dismissed, DismissedAt)
            VALUES (@t, @k, @f, @j, @d, @da);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@t", hit.Ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@k", hit.SignalKind);
        cmd.Parameters.AddWithValue("@f", Sql.Ts(hit.FiredAt));
        cmd.Parameters.AddWithValue("@j", hit.InputsJson);
        cmd.Parameters.AddWithValue("@d", hit.Dismissed ? 1 : 0);
        cmd.Parameters.AddWithValue("@da", hit.DismissedAt is DateTimeOffset t ? Sql.Ts(t) : DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public void Dismiss(long id, DateTimeOffset at)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE SignalHit SET Dismissed=1, DismissedAt=@a WHERE Id=@id";
        cmd.Parameters.AddWithValue("@a", Sql.Ts(at));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public List<SignalHit> GetAll(bool includeDismissed = false)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = includeDismissed
            ? "SELECT * FROM SignalHit ORDER BY FiredAt DESC"
            : "SELECT * FROM SignalHit WHERE Dismissed=0 ORDER BY FiredAt DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<SignalHit>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public List<SignalHit> GetForTicker(string ticker, bool includeDismissed = false)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = includeDismissed
            ? "SELECT * FROM SignalHit WHERE Ticker=@t ORDER BY FiredAt DESC"
            : "SELECT * FROM SignalHit WHERE Ticker=@t AND Dismissed=0 ORDER BY FiredAt DESC";
        cmd.Parameters.AddWithValue("@t", ticker);
        using var r = cmd.ExecuteReader();
        var list = new List<SignalHit>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// The dedupe gate: the newest hit for (ticker, kind), dismissed or not.
    public SignalHit? GetLatest(string ticker, string kind)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM SignalHit WHERE Ticker=@t AND SignalKind=@k ORDER BY FiredAt DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@t", ticker);
        cmd.Parameters.AddWithValue("@k", kind);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    private static SignalHit Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        Ticker = r.GetString(r.GetOrdinal("Ticker")),
        SignalKind = r.GetString(r.GetOrdinal("SignalKind")),
        FiredAt = Sql.ToTs(r.GetString(r.GetOrdinal("FiredAt"))),
        InputsJson = r.GetString(r.GetOrdinal("InputsJson")),
        Dismissed = r.GetInt32(r.GetOrdinal("Dismissed")) != 0,
        DismissedAt = r.IsDBNull(r.GetOrdinal("DismissedAt"))
            ? null : Sql.ToTs(r.GetString(r.GetOrdinal("DismissedAt"))),
    };
}
