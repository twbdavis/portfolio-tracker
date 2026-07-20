namespace PortfolioTracker.Core.Models;

/// One import of one account's rows. A CSV containing several accounts
/// produces one batch per account so a batch delete never crosses accounts.
public class ImportBatch
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public string FileName { get; set; } = "";
    public int RowsImported { get; set; }
    public int RowsSkipped { get; set; }
    public int RowsDeduped { get; set; }
}
