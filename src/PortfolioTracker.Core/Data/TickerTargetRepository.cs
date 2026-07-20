using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class TickerTargetRepository(Database db)
{
    public TickerTarget? GetByTicker(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM TickerTarget WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public List<TickerTarget> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM TickerTarget ORDER BY Ticker";
        using var r = cmd.ExecuteReader();
        var list = new List<TickerTarget>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// Insert-or-update by ticker (one target per ticker, enforced by schema).
    public void Upsert(TickerTarget t)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TickerTarget (Ticker, TargetPrice, TargetDirection, AlertEnabled, AlertTriggeredAt, Notes)
            VALUES (@t, @tp, @td, @ae, @at, @no)
            ON CONFLICT(Ticker) DO UPDATE SET TargetPrice=@tp, TargetDirection=@td,
                AlertEnabled=@ae, AlertTriggeredAt=@at, Notes=@no
            """;
        cmd.Parameters.AddWithValue("@t", t.Ticker);
        cmd.Parameters.AddWithValue("@tp", t.TargetPrice is null ? DBNull.Value : Sql.Dec(t.TargetPrice.Value));
        cmd.Parameters.AddWithValue("@td", (int)t.TargetDirection);
        cmd.Parameters.AddWithValue("@ae", t.AlertEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@at", t.AlertTriggeredAt is null ? DBNull.Value : Sql.Ts(t.AlertTriggeredAt.Value));
        cmd.Parameters.AddWithValue("@no", t.Notes.OrDbNull());
        cmd.ExecuteNonQuery();
    }

    public void Delete(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM TickerTarget WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker);
        cmd.ExecuteNonQuery();
    }

    public void StampAlert(string ticker, DateTimeOffset? at)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE TickerTarget SET AlertTriggeredAt=@at WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@at", at is null ? DBNull.Value : Sql.Ts(at.Value));
        cmd.Parameters.AddWithValue("@t", ticker);
        cmd.ExecuteNonQuery();
    }

    private static TickerTarget Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        Ticker = r.GetString(r.GetOrdinal("Ticker")),
        TargetPrice = r.IsDBNull(r.GetOrdinal("TargetPrice")) ? null : Sql.ToDec(r.GetString(r.GetOrdinal("TargetPrice"))),
        TargetDirection = (TargetDirection)r.GetInt32(r.GetOrdinal("TargetDirection")),
        AlertEnabled = r.GetInt32(r.GetOrdinal("AlertEnabled")) != 0,
        AlertTriggeredAt = r.IsDBNull(r.GetOrdinal("AlertTriggeredAt")) ? null : Sql.ToTs(r.GetString(r.GetOrdinal("AlertTriggeredAt"))),
        Notes = r.AsString(r.GetOrdinal("Notes")),
    };
}
