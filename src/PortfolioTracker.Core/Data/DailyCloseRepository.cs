using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class DailyCloseRepository(Database db)
{
    /// Precedence: Stooq rows are official closes and may replace a same-day
    /// live approximation; a LiveSnapshot may refresh an earlier LiveSnapshot
    /// (converging on the real close through the day) but never a Stooq row.
    public void UpsertMany(string ticker, IEnumerable<DailyCloseRow> rows)
    {
        using var conn = db.Open();
        using var txn = conn.BeginTransaction();
        foreach (var c in rows)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
                INSERT INTO DailyClose (Ticker, Date, Close, Source)
                VALUES (@t, @d, @c, @s)
                ON CONFLICT(Ticker, Date) DO UPDATE SET
                    Close=excluded.Close, Source=excluded.Source
                WHERE excluded.Source = 0 OR DailyClose.Source = 1
                """;
            cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
            cmd.Parameters.AddWithValue("@d", Sql.Date(c.Date));
            cmd.Parameters.AddWithValue("@c", Sql.Dec(c.Close));
            cmd.Parameters.AddWithValue("@s", (int)c.Source);
            cmd.ExecuteNonQuery();
        }
        txn.Commit();
    }

    /// Closes for one ticker ordered oldest-first, optionally bounded below.
    public List<DailyCloseRow> GetForTicker(string ticker, DateOnly? from = null)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = from is null
            ? "SELECT Ticker, Date, Close, Source FROM DailyClose WHERE Ticker=@t ORDER BY Date"
            : "SELECT Ticker, Date, Close, Source FROM DailyClose WHERE Ticker=@t AND Date >= @f ORDER BY Date";
        cmd.Parameters.AddWithValue("@t", ticker);
        if (from is DateOnly f) cmd.Parameters.AddWithValue("@f", Sql.Date(f));
        using var r = cmd.ExecuteReader();
        var list = new List<DailyCloseRow>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// Recent closes for every ticker, keyed by ticker, oldest-first — one
    /// query for the whole warehouse so sector rollups never do N queries.
    public Dictionary<string, List<DailyCloseRow>> GetAllByTicker(DateOnly from)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker, Date, Close, Source FROM DailyClose WHERE Date >= @f ORDER BY Ticker, Date";
        cmd.Parameters.AddWithValue("@f", Sql.Date(from));
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<string, List<DailyCloseRow>>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            var row = Map(r);
            if (!result.TryGetValue(row.Ticker, out var list))
                result[row.Ticker] = list = new List<DailyCloseRow>();
            list.Add(row);
        }
        return result;
    }

    public DateOnly? GetLatestDate(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(Date) FROM DailyClose WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker);
        var v = cmd.ExecuteScalar();
        return v is string s ? Sql.ToDate(s) : null;
    }

    public int CountForTicker(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DailyClose WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static DailyCloseRow Map(SqliteDataReader r) => new()
    {
        Ticker = r.GetString(0),
        Date = Sql.ToDate(r.GetString(1)),
        Close = Sql.ToDec(r.GetString(2)),
        Source = (CloseSource)r.GetInt32(3),
    };
}
