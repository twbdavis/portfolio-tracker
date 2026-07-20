using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class UniverseRepository(Database db)
{
    public long Insert(string name, bool isSeeded)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Universe (Name, IsSeeded, CreatedAt) VALUES (@n, @s, @c);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@s", isSeeded ? 1 : 0);
        cmd.Parameters.AddWithValue("@c", Sql.Ts(DateTimeOffset.Now));
        return (long)cmd.ExecuteScalar()!;
    }

    public void Rename(long id, string name)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Universe SET Name=@n WHERE Id=@id";
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        // members cascade, but the pragma is off by default per connection
        cmd.CommandText = "DELETE FROM UniverseMember WHERE UniverseId=@id; DELETE FROM Universe WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public List<Universe> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, IsSeeded, CreatedAt FROM Universe ORDER BY Name";
        using var r = cmd.ExecuteReader();
        var list = new List<Universe>();
        while (r.Read())
            list.Add(new Universe
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                IsSeeded = r.GetInt32(2) != 0,
                CreatedAt = Sql.ToTs(r.GetString(3)),
            });
        return list;
    }

    public Universe? Get(long id) => GetAll().FirstOrDefault(u => u.Id == id);

    public bool AnySeeded()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Universe WHERE IsSeeded=1";
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public List<string> GetMembers(long universeId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Ticker FROM UniverseMember WHERE UniverseId=@id ORDER BY Ticker";
        cmd.Parameters.AddWithValue("@id", universeId);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public Dictionary<long, int> GetMemberCounts()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT UniverseId, COUNT(*) FROM UniverseMember GROUP BY UniverseId";
        using var r = cmd.ExecuteReader();
        var counts = new Dictionary<long, int>();
        while (r.Read()) counts[r.GetInt64(0)] = r.GetInt32(1);
        return counts;
    }

    public void AddMembers(long universeId, IEnumerable<string> tickers)
    {
        using var conn = db.Open();
        using var txn = conn.BeginTransaction();
        foreach (var t in tickers)
        {
            var ticker = t.Trim().ToUpperInvariant();
            if (ticker.Length == 0) continue;
            var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = "INSERT OR IGNORE INTO UniverseMember (UniverseId, Ticker) VALUES (@u, @t)";
            cmd.Parameters.AddWithValue("@u", universeId);
            cmd.Parameters.AddWithValue("@t", ticker);
            cmd.ExecuteNonQuery();
        }
        txn.Commit();
    }

    public void RemoveMember(long universeId, string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM UniverseMember WHERE UniverseId=@u AND Ticker=@t";
        cmd.Parameters.AddWithValue("@u", universeId);
        cmd.Parameters.AddWithValue("@t", ticker);
        cmd.ExecuteNonQuery();
    }

    /// Every ticker in any universe — the crawler's full scope.
    public List<string> GetAllTickers()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Ticker FROM UniverseMember ORDER BY Ticker";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
