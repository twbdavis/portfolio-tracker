namespace PortfolioTracker.Core;

public static class HumanNumber
{
    /// Compact dollars: $1.2T, $45.3B, $890M; below a million, plain $12,345.
    public static string Money(decimal dollars)
    {
        var sign = dollars < 0 ? "-" : "";
        var abs = Math.Abs(dollars);
        if (abs >= 1_000_000_000_000m) return $"{sign}${Compact(abs / 1_000_000_000_000m)}T";
        if (abs >= 1_000_000_000m) return $"{sign}${Compact(abs / 1_000_000_000m)}B";
        if (abs >= 1_000_000m) return $"{sign}${Compact(abs / 1_000_000m)}M";
        return $"{sign}${abs:#,##0}";
    }

    // one decimal under 100 ($45.3B), none above ($890M); no trailing ".0"
    private static string Compact(decimal v) => v < 100m ? v.ToString("0.#") : v.ToString("#,##0");
}
