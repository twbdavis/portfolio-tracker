using System.Globalization;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

/// Filing status and estimated other income, stored as key/value in SQLite.
public class TaxSettingsRepository(Database db)
{
    public FilingStatus GetFilingStatus() =>
        Get("FilingStatus") is string s && Enum.TryParse<FilingStatus>(s, out var f) ? f : FilingStatus.Single;

    public void SetFilingStatus(FilingStatus status) => Set("FilingStatus", status.ToString());

    public decimal GetOtherIncome() =>
        Get("OtherIncome") is string s ? decimal.Parse(s, CultureInfo.InvariantCulture) : 0m;

    public void SetOtherIncome(decimal amount) =>
        Set("OtherIncome", amount.ToString(CultureInfo.InvariantCulture));

    private string? Get(string key)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM TaxSetting WHERE Key=@k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void Set(string key, string value)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TaxSetting (Key, Value) VALUES (@k, @v)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value
            """;
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }
}

public class TaxCarryforwardRepository(Database db)
{
    public TaxCarryforward? Get(int year)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Year, ShortTerm, LongTerm, IsManual FROM TaxCarryforward WHERE Year=@y";
        cmd.Parameters.AddWithValue("@y", year);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public List<TaxCarryforward> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Year, ShortTerm, LongTerm, IsManual FROM TaxCarryforward ORDER BY Year";
        using var r = cmd.ExecuteReader();
        var list = new List<TaxCarryforward>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// Computed refreshes never overwrite a manual row; a manual save always wins.
    public void Upsert(TaxCarryforward c)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = c.IsManual
            ? """
              INSERT INTO TaxCarryforward (Year, ShortTerm, LongTerm, IsManual) VALUES (@y, @s, @l, 1)
              ON CONFLICT(Year) DO UPDATE SET ShortTerm=excluded.ShortTerm, LongTerm=excluded.LongTerm, IsManual=1
              """
            : """
              INSERT INTO TaxCarryforward (Year, ShortTerm, LongTerm, IsManual) VALUES (@y, @s, @l, 0)
              ON CONFLICT(Year) DO UPDATE SET ShortTerm=excluded.ShortTerm, LongTerm=excluded.LongTerm
                  WHERE TaxCarryforward.IsManual = 0
              """;
        cmd.Parameters.AddWithValue("@y", c.Year);
        cmd.Parameters.AddWithValue("@s", Sql.Dec(c.ShortTerm));
        cmd.Parameters.AddWithValue("@l", Sql.Dec(c.LongTerm));
        cmd.ExecuteNonQuery();
    }

    public void ClearManual(int year)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE TaxCarryforward SET IsManual=0 WHERE Year=@y";
        cmd.Parameters.AddWithValue("@y", year);
        cmd.ExecuteNonQuery();
    }

    private static TaxCarryforward Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Year = r.GetInt32(0),
        ShortTerm = Sql.ToDec(r.GetString(1)),
        LongTerm = Sql.ToDec(r.GetString(2)),
        IsManual = r.GetInt32(3) == 1,
    };
}
