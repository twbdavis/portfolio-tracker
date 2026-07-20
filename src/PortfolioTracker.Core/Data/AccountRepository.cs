using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class AccountRepository(Database db)
{
    public long Insert(Account a)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Account (AccountNumber, AccountNameRaw, DisplayName, TaxTreatment, CreatedAt, SortOrder, NeedsReview)
            VALUES (@n, @raw, @d, @t, @at, @s, @r);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@n", a.AccountNumber);
        cmd.Parameters.AddWithValue("@raw", a.AccountNameRaw.OrDbNull());
        cmd.Parameters.AddWithValue("@d", a.DisplayName);
        cmd.Parameters.AddWithValue("@t", (int)a.TaxTreatment);
        cmd.Parameters.AddWithValue("@at", Sql.Ts(a.CreatedAt));
        cmd.Parameters.AddWithValue("@s", a.SortOrder);
        cmd.Parameters.AddWithValue("@r", a.NeedsReview ? 1 : 0);
        a.Id = (long)cmd.ExecuteScalar()!;
        return a.Id;
    }

    public void Update(Account a)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Account SET AccountNumber=@n, AccountNameRaw=@raw, DisplayName=@d,
                TaxTreatment=@t, SortOrder=@s, NeedsReview=@r WHERE Id=@id
            """;
        cmd.Parameters.AddWithValue("@n", a.AccountNumber);
        cmd.Parameters.AddWithValue("@raw", a.AccountNameRaw.OrDbNull());
        cmd.Parameters.AddWithValue("@d", a.DisplayName);
        cmd.Parameters.AddWithValue("@t", (int)a.TaxTreatment);
        cmd.Parameters.AddWithValue("@s", a.SortOrder);
        cmd.Parameters.AddWithValue("@r", a.NeedsReview ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", a.Id);
        cmd.ExecuteNonQuery();
    }

    /// Row delete only — callers must cascade the account's data first
    /// (ManualEntryService.DeleteAccount does).
    public void Delete(long id)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Account WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public List<Account> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Account ORDER BY SortOrder, Id";
        using var r = cmd.ExecuteReader();
        var list = new List<Account>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public Account? GetById(long id) => GetAll().FirstOrDefault(a => a.Id == id);

    public Account? GetByNumber(string accountNumber) =>
        GetAll().FirstOrDefault(a => AccountNumbers.Match(a.AccountNumber, accountNumber));

    private static Account Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        AccountNumber = r.GetString(r.GetOrdinal("AccountNumber")),
        AccountNameRaw = r.AsString(r.GetOrdinal("AccountNameRaw")),
        DisplayName = r.GetString(r.GetOrdinal("DisplayName")),
        TaxTreatment = (TaxTreatment)r.GetInt32(r.GetOrdinal("TaxTreatment")),
        CreatedAt = Sql.ToTs(r.GetString(r.GetOrdinal("CreatedAt"))),
        SortOrder = r.GetInt32(r.GetOrdinal("SortOrder")),
        NeedsReview = r.GetInt32(r.GetOrdinal("NeedsReview")) != 0,
    };
}
