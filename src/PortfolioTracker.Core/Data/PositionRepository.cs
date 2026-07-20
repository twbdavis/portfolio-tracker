using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class PositionRepository(Database db)
{
    public Position? GetByAccountAndTicker(long accountId, string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Position WHERE AccountId = @a AND Ticker = @t";
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@t", ticker);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// The same ticker across every account (cross-account wash matching).
    public List<Position> GetAllByTicker(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Position WHERE Ticker = @t ORDER BY AccountId";
        cmd.Parameters.AddWithValue("@t", ticker);
        return ReadAll(cmd);
    }

    public Position GetOrCreate(long accountId, string ticker, string? cusip, string? name)
    {
        var existing = GetByAccountAndTicker(accountId, ticker);
        if (existing is not null)
        {
            // backfill cusip/name if we just learned them
            if ((existing.Cusip is null && cusip is not null) || (existing.Name is null && name is not null))
            {
                existing.Cusip ??= cusip;
                existing.Name ??= name;
                Update(existing);
            }
            return existing;
        }
        using (var conn = db.Open())
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Position (AccountId, Ticker, Cusip, Name) VALUES (@a, @t, @c, @n)";
            cmd.Parameters.AddWithValue("@a", accountId);
            cmd.Parameters.AddWithValue("@t", ticker);
            cmd.Parameters.AddWithValue("@c", cusip.OrDbNull());
            cmd.Parameters.AddWithValue("@n", name.OrDbNull());
            cmd.ExecuteNonQuery();
        }
        return GetByAccountAndTicker(accountId, ticker)!;
    }

    public List<Position> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Position ORDER BY Ticker, AccountId";
        return ReadAll(cmd);
    }

    public List<Position> GetByAccount(long accountId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Position WHERE AccountId = @a ORDER BY Ticker";
        cmd.Parameters.AddWithValue("@a", accountId);
        return ReadAll(cmd);
    }

    public void Update(Position p)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Position SET Cusip=@c, Name=@n, NeedsOpeningBalance=@nob, Notes=@no
            WHERE Id=@id
            """;
        cmd.Parameters.AddWithValue("@c", p.Cusip.OrDbNull());
        cmd.Parameters.AddWithValue("@n", p.Name.OrDbNull());
        cmd.Parameters.AddWithValue("@nob", p.NeedsOpeningBalance ? 1 : 0);
        cmd.Parameters.AddWithValue("@no", p.Notes.OrDbNull());
        cmd.Parameters.AddWithValue("@id", p.Id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Position WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetNeedsOpeningBalance(long id, bool value)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Position SET NeedsOpeningBalance=@v WHERE Id=@id";
        cmd.Parameters.AddWithValue("@v", value ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    private static List<Position> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Position>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static Position Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        AccountId = r.GetInt64(r.GetOrdinal("AccountId")),
        Ticker = r.GetString(r.GetOrdinal("Ticker")),
        Cusip = r.AsString(r.GetOrdinal("Cusip")),
        Name = r.AsString(r.GetOrdinal("Name")),
        NeedsOpeningBalance = r.GetInt32(r.GetOrdinal("NeedsOpeningBalance")) != 0,
        Notes = r.AsString(r.GetOrdinal("Notes")),
    };
}
