using System.Text.Json;
using System.Text.Json.Serialization;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tax;

public class BracketTier
{
    public decimal? UpTo { get; set; }               // null = top tier
    public decimal Rate { get; set; }                // 0.22 = 22%
}

public class YearBrackets
{
    public Dictionary<FilingStatus, decimal> StandardDeduction { get; set; } = new();
    public Dictionary<FilingStatus, List<BracketTier>> OrdinaryBrackets { get; set; } = new();
    public Dictionary<FilingStatus, List<BracketTier>> LtcgBrackets { get; set; } = new();
}

/// Editable bracket data in the app data folder — future tax years are a data
/// update (edit taxbrackets.json), not a code change. Seeded with 2026 values.
public class TaxBracketsFile
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<int, YearBrackets> Years { get; set; } = new();

    public static TaxBracketsFile Load(string path)
    {
        if (File.Exists(path))
        {
            var loaded = JsonSerializer.Deserialize<TaxBracketsFile>(File.ReadAllText(path), JsonOpts);
            if (loaded is { Years.Count: > 0 }) return loaded;
        }
        var seeded = Seed2026();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(seeded, JsonOpts));
        return seeded;
    }

    /// Exact year if present, otherwise the nearest earlier year (brackets only
    /// change by inflation adjustment), otherwise the earliest on file.
    public YearBrackets ForYear(int year)
    {
        if (Years.TryGetValue(year, out var exact)) return exact;
        var best = Years.Keys.Where(y => y < year).DefaultIfEmpty(Years.Keys.Min()).Max();
        return Years[best];
    }

    public bool HasYear(int year) => Years.ContainsKey(year);

    /// 2026 values: ordinary brackets per IRS Rev. Proc. 2025-32, LTCG breakpoints
    /// and standard deduction per the same revenue procedure.
    public static TaxBracketsFile Seed2026()
    {
        static List<BracketTier> Ord(params (decimal? upTo, decimal rate)[] tiers) =>
            tiers.Select(t => new BracketTier { UpTo = t.upTo, Rate = t.rate }).ToList();
        return new TaxBracketsFile
        {
            Years =
            {
                [2026] = new YearBrackets
                {
                    StandardDeduction =
                    {
                        [FilingStatus.Single] = 16_100m,
                        [FilingStatus.MarriedFilingJointly] = 32_200m,
                        [FilingStatus.HeadOfHousehold] = 24_150m,
                    },
                    OrdinaryBrackets =
                    {
                        [FilingStatus.Single] = Ord((12_400m, 0.10m), (50_400m, 0.12m), (105_700m, 0.22m),
                            (201_775m, 0.24m), (256_225m, 0.32m), (640_600m, 0.35m), (null, 0.37m)),
                        [FilingStatus.MarriedFilingJointly] = Ord((24_800m, 0.10m), (100_800m, 0.12m), (211_400m, 0.22m),
                            (403_550m, 0.24m), (512_450m, 0.32m), (768_700m, 0.35m), (null, 0.37m)),
                        [FilingStatus.HeadOfHousehold] = Ord((17_700m, 0.10m), (67_450m, 0.12m), (105_700m, 0.22m),
                            (201_775m, 0.24m), (256_225m, 0.32m), (640_600m, 0.35m), (null, 0.37m)),
                    },
                    LtcgBrackets =
                    {
                        [FilingStatus.Single] = Ord((49_450m, 0m), (545_500m, 0.15m), (null, 0.20m)),
                        [FilingStatus.MarriedFilingJointly] = Ord((98_900m, 0m), (613_700m, 0.15m), (null, 0.20m)),
                        [FilingStatus.HeadOfHousehold] = Ord((66_200m, 0m), (579_600m, 0.15m), (null, 0.20m)),
                    },
                },
            },
        };
    }
}
