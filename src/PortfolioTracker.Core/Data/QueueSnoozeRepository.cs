namespace PortfolioTracker.Core.Data;

public class QueueSnoozeRepository(Database db)
{
    public void Snooze(string ticker, DateTimeOffset until)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO QueueSnooze (Ticker, SnoozedUntil) VALUES (@t, @u)
            ON CONFLICT(Ticker) DO UPDATE SET SnoozedUntil=excluded.SnoozedUntil
            """;
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@u", Sql.Ts(until));
        cmd.ExecuteNonQuery();
    }

    public void Unsnooze(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM QueueSnooze WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.ExecuteNonQuery();
    }

    /// Tickers still snoozed as of `now`; expired rows are pruned on read so
    /// the table can't grow unbounded.
    public Dictionary<string, DateTimeOffset> GetActive(DateTimeOffset now)
    {
        using var conn = db.Open();
        var prune = conn.CreateCommand();
        prune.CommandText = "DELETE FROM QueueSnooze WHERE SnoozedUntil <= @n";
        prune.Parameters.AddWithValue("@n", Sql.Ts(now));
        prune.ExecuteNonQuery();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker, SnoozedUntil FROM QueueSnooze";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) map[r.GetString(0)] = Sql.ToTs(r.GetString(1));
        return map;
    }
}
