namespace PortfolioTracker.Core.Data;

/// Small key/value store for service bookkeeping (crawler progress stamps,
/// last signal evaluation, ...) — nothing here is user data.
public class AppStateRepository(Database db)
{
    public string? Get(string key)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppState WHERE Key=@k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AppState (Key, Value) VALUES (@k, @v)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value
            """;
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }
}
