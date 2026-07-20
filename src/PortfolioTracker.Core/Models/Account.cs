namespace PortfolioTracker.Core.Models;

/// One brokerage account from the Chase export. AccountNumber is the masked
/// CSV value ("...1234"); the migration placeholder for pre-account data uses
/// "" until an import with overlapping rows adopts it or the user sets it.
public class Account
{
    public long Id { get; set; }
    public string AccountNumber { get; set; } = "";
    public string? AccountNameRaw { get; set; }      // CSV `Account Name`, e.g. "Self-Directed-Ret"
    public string DisplayName { get; set; } = "";
    public TaxTreatment TaxTreatment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int SortOrder { get; set; }
    public bool NeedsReview { get; set; }            // set by migration; cleared after the first-launch prompt

    public bool IsRetirement => TaxTreatment != TaxTreatment.Taxable;
}
