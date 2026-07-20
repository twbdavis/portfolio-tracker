using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class ImportBatchRepository(Database db)
{
    public long Insert(ImportBatch b)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ImportBatch (AccountId, ImportedAt, FileName, RowsImported, RowsSkipped, RowsDeduped)
            VALUES (@a, @at, @f, @i, @s, @d);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@a", b.AccountId);
        cmd.Parameters.AddWithValue("@at", Sql.Ts(b.ImportedAt));
        cmd.Parameters.AddWithValue("@f", b.FileName);
        cmd.Parameters.AddWithValue("@i", b.RowsImported);
        cmd.Parameters.AddWithValue("@s", b.RowsSkipped);
        cmd.Parameters.AddWithValue("@d", b.RowsDeduped);
        return (long)cmd.ExecuteScalar()!;
    }

    public void Update(ImportBatch b)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ImportBatch SET RowsImported=@i, RowsSkipped=@s, RowsDeduped=@d WHERE Id=@id";
        cmd.Parameters.AddWithValue("@i", b.RowsImported);
        cmd.Parameters.AddWithValue("@s", b.RowsSkipped);
        cmd.Parameters.AddWithValue("@d", b.RowsDeduped);
        cmd.Parameters.AddWithValue("@id", b.Id);
        cmd.ExecuteNonQuery();
    }

    public List<ImportBatch> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ImportBatch ORDER BY ImportedAt DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<ImportBatch>();
        while (r.Read()) list.Add(new ImportBatch
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            AccountId = r.GetInt64(r.GetOrdinal("AccountId")),
            ImportedAt = Sql.ToTs(r.GetString(r.GetOrdinal("ImportedAt"))),
            FileName = r.GetString(r.GetOrdinal("FileName")),
            RowsImported = r.GetInt32(r.GetOrdinal("RowsImported")),
            RowsSkipped = r.GetInt32(r.GetOrdinal("RowsSkipped")),
            RowsDeduped = r.GetInt32(r.GetOrdinal("RowsDeduped")),
        });
        return list;
    }

    public void Delete(long id)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ImportBatch WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteByAccount(long accountId)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ImportBatch WHERE AccountId=@a";
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.ExecuteNonQuery();
    }
}
