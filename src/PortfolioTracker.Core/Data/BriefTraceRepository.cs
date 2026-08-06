using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class BriefTraceRepository(Database db)
{
    public long Insert(BriefTraceRow row)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BriefTrace (BriefId, Ticker, SnapshotHash, Section, Stage, UserPrompt,
                RawResponse, CleanedText, ViolationsJson, Passed, CreatedAt)
            VALUES (@b, @t, @h, @s, @st, @p, @r, @c, @v, @pa, @at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@b", row.BriefId.OrDbNull());
        cmd.Parameters.AddWithValue("@t", row.Ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@h", row.SnapshotHash);
        cmd.Parameters.AddWithValue("@s", row.Section);
        cmd.Parameters.AddWithValue("@st", row.Stage);
        cmd.Parameters.AddWithValue("@p", row.UserPrompt);
        cmd.Parameters.AddWithValue("@r", row.RawResponse);
        cmd.Parameters.AddWithValue("@c", row.CleanedText);
        cmd.Parameters.AddWithValue("@v", row.ViolationsJson);
        cmd.Parameters.AddWithValue("@pa", row.Passed ? 1 : 0);
        cmd.Parameters.AddWithValue("@at", Sql.Ts(row.CreatedAt));
        return (long)cmd.ExecuteScalar()!;
    }

    public List<BriefTraceRow> GetForBrief(long briefId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM BriefTrace WHERE BriefId=@b ORDER BY Id";
        cmd.Parameters.AddWithValue("@b", briefId);
        return ReadAll(cmd);
    }

    /// Orphan rows for one input snapshot — a brief that was interrupted
    /// mid-pipeline. Passing sections here are the resume points.
    public List<BriefTraceRow> GetUnlinked(string ticker, string snapshotHash)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM BriefTrace
            WHERE Ticker=@t AND SnapshotHash=@h AND BriefId IS NULL ORDER BY Id
            """;
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@h", snapshotHash);
        return ReadAll(cmd);
    }

    /// Claims the orphan rows for a just-persisted brief.
    public void LinkToBrief(string ticker, string snapshotHash, long briefId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE BriefTrace SET BriefId=@b
            WHERE Ticker=@t AND SnapshotHash=@h AND BriefId IS NULL
            """;
        cmd.Parameters.AddWithValue("@b", briefId);
        cmd.Parameters.AddWithValue("@t", ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@h", snapshotHash);
        cmd.ExecuteNonQuery();
    }

    private static List<BriefTraceRow> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<BriefTraceRow>();
        while (r.Read())
            list.Add(new BriefTraceRow
            {
                Id = r.GetInt64(r.GetOrdinal("Id")),
                BriefId = r.AsLong(r.GetOrdinal("BriefId")),
                Ticker = r.GetString(r.GetOrdinal("Ticker")),
                SnapshotHash = r.GetString(r.GetOrdinal("SnapshotHash")),
                Section = r.GetString(r.GetOrdinal("Section")),
                Stage = r.GetString(r.GetOrdinal("Stage")),
                UserPrompt = r.GetString(r.GetOrdinal("UserPrompt")),
                RawResponse = r.GetString(r.GetOrdinal("RawResponse")),
                CleanedText = r.GetString(r.GetOrdinal("CleanedText")),
                ViolationsJson = r.GetString(r.GetOrdinal("ViolationsJson")),
                Passed = r.GetInt32(r.GetOrdinal("Passed")) != 0,
                CreatedAt = Sql.ToTs(r.GetString(r.GetOrdinal("CreatedAt"))),
            });
        return list;
    }
}
