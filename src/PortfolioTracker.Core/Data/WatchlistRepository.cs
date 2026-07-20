using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class WatchlistRepository(Database db)
{
    public long Insert(WatchlistItem w)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO WatchlistItem (Ticker, Name, AddedAt, Thesis, Conviction, TargetBuyPrice,
                AlertEnabled, AlertTriggeredAt, Notes)
            VALUES (@t, @n, @a, @th, @c, @tp, @ae, @at, @no);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, w);
        return (long)cmd.ExecuteScalar()!;
    }

    public void Update(WatchlistItem w)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE WatchlistItem SET Ticker=@t, Name=@n, AddedAt=@a, Thesis=@th, Conviction=@c,
                TargetBuyPrice=@tp, AlertEnabled=@ae, AlertTriggeredAt=@at, Notes=@no WHERE Id=@id
            """;
        Bind(cmd, w);
        cmd.Parameters.AddWithValue("@id", w.Id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM WatchlistItem WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void StampAlert(long id, DateTimeOffset? triggeredAt)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE WatchlistItem SET AlertTriggeredAt=@at WHERE Id=@id";
        cmd.Parameters.AddWithValue("@at", triggeredAt is DateTimeOffset t ? Sql.Ts(t) : DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public WatchlistItem? GetByTicker(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM WatchlistItem WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public List<WatchlistItem> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM WatchlistItem ORDER BY Ticker";
        using var r = cmd.ExecuteReader();
        var list = new List<WatchlistItem>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static void Bind(SqliteCommand cmd, WatchlistItem w)
    {
        cmd.Parameters.AddWithValue("@t", w.Ticker);
        cmd.Parameters.AddWithValue("@n", w.Name.OrDbNull());
        cmd.Parameters.AddWithValue("@a", Sql.Ts(w.AddedAt));
        cmd.Parameters.AddWithValue("@th", w.Thesis.OrDbNull());
        cmd.Parameters.AddWithValue("@c", w.Conviction);
        cmd.Parameters.AddWithValue("@tp", w.TargetBuyPrice is decimal p ? Sql.Dec(p) : DBNull.Value);
        cmd.Parameters.AddWithValue("@ae", w.AlertEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@at", w.AlertTriggeredAt is DateTimeOffset t ? Sql.Ts(t) : DBNull.Value);
        cmd.Parameters.AddWithValue("@no", w.Notes.OrDbNull());
    }

    private static WatchlistItem Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        Ticker = r.GetString(r.GetOrdinal("Ticker")),
        Name = r.AsString(r.GetOrdinal("Name")),
        AddedAt = Sql.ToTs(r.GetString(r.GetOrdinal("AddedAt"))),
        Thesis = r.AsString(r.GetOrdinal("Thesis")),
        Conviction = r.GetInt32(r.GetOrdinal("Conviction")),
        TargetBuyPrice = r.IsDBNull(r.GetOrdinal("TargetBuyPrice"))
            ? null : Sql.ToDec(r.GetString(r.GetOrdinal("TargetBuyPrice"))),
        AlertEnabled = r.GetInt32(r.GetOrdinal("AlertEnabled")) != 0,
        AlertTriggeredAt = r.IsDBNull(r.GetOrdinal("AlertTriggeredAt"))
            ? null : Sql.ToTs(r.GetString(r.GetOrdinal("AlertTriggeredAt"))),
        Notes = r.AsString(r.GetOrdinal("Notes")),
    };
}
