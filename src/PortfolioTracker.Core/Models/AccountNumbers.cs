namespace PortfolioTracker.Core.Models;

/// Chase CSVs mask account numbers ("...1234") but users enter them by hand
/// too ("1234", "***1234"). Matching compares only the letters and digits so
/// every masking style resolves to the same account instead of creating a
/// duplicate on import.
public static class AccountNumbers
{
    public static string Normalize(string number) =>
        string.Concat(number.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    public static bool Match(string a, string b) => Normalize(a) == Normalize(b);
}
