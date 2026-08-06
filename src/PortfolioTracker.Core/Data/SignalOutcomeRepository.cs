using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class SignalOutcomeRepository(Database db)
{
    public void Upsert(SignalOutcomeRow row)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SignalOutcome (SignalHitId, Ticker, SignalKind, Sector, FireDate,
                TickerCloseAtFire, SectorClosesJson,
                Return30, Excess30, Return60, Excess60, Return90, Excess90)
            VALUES (@id, @t, @k, @s, @fd, @c, @j, @r30, @e30, @r60, @e60, @r90, @e90)
            ON CONFLICT(SignalHitId) DO UPDATE SET
                TickerCloseAtFire=excluded.TickerCloseAtFire,
                SectorClosesJson=excluded.SectorClosesJson,
                Return30=excluded.Return30, Excess30=excluded.Excess30,
                Return60=excluded.Return60, Excess60=excluded.Excess60,
                Return90=excluded.Return90, Excess90=excluded.Excess90
            """;
        cmd.Parameters.AddWithValue("@id", row.SignalHitId);
        cmd.Parameters.AddWithValue("@t", row.Ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@k", row.SignalKind);
        cmd.Parameters.AddWithValue("@s", row.Sector.OrDbNull());
        cmd.Parameters.AddWithValue("@fd", Sql.Date(row.FireDate));
        cmd.Parameters.AddWithValue("@c", Dec(row.TickerCloseAtFire));
        cmd.Parameters.AddWithValue("@j", row.SectorClosesJson.OrDbNull());
        cmd.Parameters.AddWithValue("@r30", Dec(row.Return30));
        cmd.Parameters.AddWithValue("@e30", Dec(row.Excess30));
        cmd.Parameters.AddWithValue("@r60", Dec(row.Return60));
        cmd.Parameters.AddWithValue("@e60", Dec(row.Excess60));
        cmd.Parameters.AddWithValue("@r90", Dec(row.Return90));
        cmd.Parameters.AddWithValue("@e90", Dec(row.Excess90));
        cmd.ExecuteNonQuery();
    }

    public List<SignalOutcomeRow> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SignalHitId, Ticker, SignalKind, Sector, FireDate, TickerCloseAtFire,
                   SectorClosesJson, Return30, Excess30, Return60, Excess60, Return90, Excess90
            FROM SignalOutcome
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<SignalOutcomeRow>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public HashSet<long> GetTrackedHitIds()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SignalHitId FROM SignalOutcome";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    }

    private static object Dec(decimal? v) => v is decimal d ? Sql.Dec(d) : DBNull.Value;

    private static SignalOutcomeRow Map(SqliteDataReader r) => new()
    {
        SignalHitId = r.GetInt64(0),
        Ticker = r.GetString(1),
        SignalKind = r.GetString(2),
        Sector = r.AsString(3),
        FireDate = Sql.ToDate(r.GetString(4)),
        TickerCloseAtFire = r.IsDBNull(5) ? null : Sql.ToDec(r.GetString(5)),
        SectorClosesJson = r.AsString(6),
        Return30 = r.IsDBNull(7) ? null : Sql.ToDec(r.GetString(7)),
        Excess30 = r.IsDBNull(8) ? null : Sql.ToDec(r.GetString(8)),
        Return60 = r.IsDBNull(9) ? null : Sql.ToDec(r.GetString(9)),
        Excess60 = r.IsDBNull(10) ? null : Sql.ToDec(r.GetString(10)),
        Return90 = r.IsDBNull(11) ? null : Sql.ToDec(r.GetString(11)),
        Excess90 = r.IsDBNull(12) ? null : Sql.ToDec(r.GetString(12)),
    };
}
