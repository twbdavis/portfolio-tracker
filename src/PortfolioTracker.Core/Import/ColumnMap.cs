namespace PortfolioTracker.Core.Import;

/// Maps the logical fields the importer needs to actual CSV header names.
public class ColumnMap(Dictionary<string, string> fieldToHeader)
{
    public static readonly string[] RequiredFields =
        ["Trade Date", "Account Name", "Account Number", "Type", "Description", "Cusip", "Ticker", "Security Type", "Price USD", "Quantity", "Amount USD"];

    public static ColumnMap Default { get; } = new(RequiredFields.ToDictionary(f => f, f => f));

    public string HeaderFor(string field) => fieldToHeader[field];
}
