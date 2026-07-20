using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class TickerClassificationRepository(Database db)
{
    /// Crawled classification never clobbers a user override.
    public void UpsertFromCrawl(string ticker, string? sector, string? industry)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TickerClassification (Ticker, Sector, Industry, Source, UpdatedAt)
            VALUES (@t, @s, @i, 0, @u)
            ON CONFLICT(Ticker) DO UPDATE SET
                Sector=excluded.Sector, Industry=excluded.Industry, UpdatedAt=excluded.UpdatedAt
            WHERE TickerClassification.Source = 0
            """;
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@s", sector.OrDbNull());
        cmd.Parameters.AddWithValue("@i", industry.OrDbNull());
        cmd.Parameters.AddWithValue("@u", Sql.Ts(DateTimeOffset.Now));
        cmd.ExecuteNonQuery();
    }

    public void SetUserOverride(string ticker, string? sector, string? industry)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TickerClassification (Ticker, Sector, Industry, Source, UpdatedAt)
            VALUES (@t, @s, @i, 1, @u)
            ON CONFLICT(Ticker) DO UPDATE SET
                Sector=excluded.Sector, Industry=excluded.Industry, Source=1, UpdatedAt=excluded.UpdatedAt
            """;
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@s", sector.OrDbNull());
        cmd.Parameters.AddWithValue("@i", industry.OrDbNull());
        cmd.Parameters.AddWithValue("@u", Sql.Ts(DateTimeOffset.Now));
        cmd.ExecuteNonQuery();
    }

    public TickerClassification? Get(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker, Sector, Industry, Source, UpdatedAt FROM TickerClassification WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public List<TickerClassification> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker, Sector, Industry, Source, UpdatedAt FROM TickerClassification";
        using var r = cmd.ExecuteReader();
        var list = new List<TickerClassification>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static TickerClassification Map(SqliteDataReader r) => new()
    {
        Ticker = r.GetString(0),
        Sector = r.AsString(1),
        Industry = r.AsString(2),
        Source = (ClassificationSource)r.GetInt32(3),
        UpdatedAt = Sql.ToTs(r.GetString(4)),
    };
}
