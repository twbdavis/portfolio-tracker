using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class ResearchBriefRepository(Database db)
{
    public long Insert(ResearchBriefRow row)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ResearchBrief (Ticker, CreatedAt, Model, InputSnapshotJson, BriefMarkdown,
                TokensUsed, NeedsReview, InputSnapshotHash)
            VALUES (@t, @c, @m, @j, @b, @u, @n, @h);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@t", row.Ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@c", Sql.Ts(row.CreatedAt));
        cmd.Parameters.AddWithValue("@m", row.Model);
        cmd.Parameters.AddWithValue("@j", row.InputSnapshotJson);
        cmd.Parameters.AddWithValue("@b", row.BriefMarkdown);
        cmd.Parameters.AddWithValue("@u", row.TokensUsed);
        cmd.Parameters.AddWithValue("@n", row.NeedsReview ? 1 : 0);
        cmd.Parameters.AddWithValue("@h", row.InputSnapshotHash.OrDbNull());
        return (long)cmd.ExecuteScalar()!;
    }

    public List<ResearchBriefRow> GetForTicker(string ticker, bool includeSnapshot = false)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, Ticker, CreatedAt, Model, {(includeSnapshot ? "InputSnapshotJson" : "''")},
                   BriefMarkdown, TokensUsed, NeedsReview, InputSnapshotHash
            FROM ResearchBrief WHERE Ticker=@t ORDER BY CreatedAt DESC
            """;
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        using var r = cmd.ExecuteReader();
        var list = new List<ResearchBriefRow>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// Every stored brief, newest first, without the heavy bodies — the
    /// Briefs tab lists history and loads full text per brief on open.
    public List<ResearchBriefRow> GetAllSummaries()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Ticker, CreatedAt, Model, '', '', TokensUsed, NeedsReview, InputSnapshotHash
            FROM ResearchBrief ORDER BY CreatedAt DESC, Id DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<ResearchBriefRow>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public ResearchBriefRow? Get(long id)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Ticker, CreatedAt, Model, InputSnapshotJson, BriefMarkdown, TokensUsed,
                   NeedsReview, InputSnapshotHash
            FROM ResearchBrief WHERE Id=@id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// Idempotency: the newest brief generated from exactly this input
    /// snapshot, if any.
    public ResearchBriefRow? FindBySnapshotHash(string ticker, string hash)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Ticker, CreatedAt, Model, InputSnapshotJson, BriefMarkdown, TokensUsed,
                   NeedsReview, InputSnapshotHash
            FROM ResearchBrief WHERE Ticker=@t AND InputSnapshotHash=@h
            ORDER BY CreatedAt DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@h", hash);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// Latest brief id per ticker, for queue-row badges in one query.
    public Dictionary<string, long> GetLatestIdByTicker()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker, MAX(Id) FROM ResearchBrief GROUP BY Ticker";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }

    private static ResearchBriefRow Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Ticker = r.GetString(1),
        CreatedAt = Sql.ToTs(r.GetString(2)),
        Model = r.GetString(3),
        InputSnapshotJson = r.GetString(4),
        BriefMarkdown = r.GetString(5),
        TokensUsed = r.GetInt32(6),
        NeedsReview = r.GetInt32(7) != 0,
        InputSnapshotHash = r.AsString(8),
    };
}
