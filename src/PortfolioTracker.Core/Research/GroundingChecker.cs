using System.Globalization;
using System.Text.RegularExpressions;

namespace PortfolioTracker.Core.Research;

public record GroundingViolation(string Kind, string Detail, string? Token = null)
{
    public const string Number = "number";
}

public record GroundingResult(bool Passed, List<GroundingViolation> Violations);

/// Programmatic grounding verification for model output. The guarantee is
/// narrow and honest: every number the model wrote exists in the data it was
/// given (within rounding and unit reformatting). It does NOT verify that the
/// reasoning about those numbers is sound, and it does NOT police word choice
/// (a vocabulary ban was removed 2026-08-05: it kept flagging the payload's
/// own analyst-rating labels, and the user wants the model's response
/// unfiltered) — both stay the reader's job.
public static class GroundingChecker
{
    /// Match if rounding both sides to this many significant figures agrees
    /// under some unit factor.
    public const int SignificantFigures = 2;

    /// Unit reformatting the model may legitimately do: percent-vs-decimal
    /// (0.62 vs 62%), and thousand/million/billion re-basing ($120B vs a
    /// market cap stored in millions).
    private static readonly decimal[] UnitFactors =
        { 1m, 100m, 0.01m, 1_000m, 0.001m, 1_000_000m, 0.000001m, 1_000_000_000m, 0.000000001m };

    // numbers with optional $, sign, commas, decimals, and %/K/M/B/T suffix
    private static readonly Regex NumberToken = new(
        @"(?<![\w.])[-+]?\$?\s?(\d{1,3}(?:,\d{3})+|\d+)(\.\d+)?\s?(%|[kKmMbBtT]\b)?",
        RegexOptions.Compiled);

    // structural text — dates, quarters, list markers — is excluded from
    // number matching entirely: these are how prose is organized, not data
    private static readonly Regex[] NonDataPatterns =
    {
        new(@"\b\d{4}-\d{2}(-\d{2})?\b", RegexOptions.Compiled),          // 2026-08-04, 2026-08
        new(@"\b\d{1,2}/\d{1,2}/\d{2,4}\b", RegexOptions.Compiled),      // 8/4/2026
        // month-name dates: "July 20, 2026", "Jan 5" — HAS failed on the "20".
        // Case-sensitive so "may 10%" in prose is never mistaken for a date.
        new(@"\b(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sept?|Oct|Nov|Dec)\.?\s+\d{1,2}(st|nd|rd|th)?\b",
            RegexOptions.Compiled),
        new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled),                  // bare years
        new(@"\bQ[1-4]\b", RegexOptions.Compiled),                        // quarters
        // SEC rule citations are jargon, not figures: "10b5-1 plans" — the
        // tokenizer otherwise reads the leading "10"
        new(@"\b10b5-1\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // period vocabulary, not data: "52-week high", "52w range"
        new(@"\b52[-\s]?w(eek|k)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // ordered-list markers ("4. The company…"): the questions section is
        // REQUIRED to be a numbered list, so its markers must never need to
        // exist in the data — TEL's questions failed twice on the "4."
        new(@"(?m)^\s{0,8}\d{1,2}[.)]\s", RegexOptions.Compiled),
        // the Buy score ("Score: 64/100") is the model's judgment by design,
        // not a quoted figure
        new(@"\b\d{1,3}\s?/\s?100\b", RegexOptions.Compiled),
    };

    public static GroundingResult Check(string output, string sliceJson, string templateText)
    {
        var violations = new List<GroundingViolation>();

        var allowed = ExtractNumbers(sliceJson);
        allowed.UnionWith(ExtractNumbers(templateText));   // format-contract literals can't false-flag

        foreach (var (raw, value) in ExtractNumberTokens(output))
            if (!Matches(value, allowed))
                violations.Add(new GroundingViolation(GroundingViolation.Number,
                    $"\"{raw}\" does not match any number in this section's data", raw));

        return new GroundingResult(violations.Count == 0, violations);
    }

    /// Distinct numeric values in a text, dates excluded.
    public static HashSet<decimal> ExtractNumbers(string text) =>
        ExtractNumberTokens(text).Select(t => t.Value).ToHashSet();

    public static List<(string Raw, decimal Value)> ExtractNumberTokens(string text)
    {
        var scrubbed = text;
        foreach (var pattern in NonDataPatterns)
            scrubbed = pattern.Replace(scrubbed, m => new string(' ', m.Length));

        var tokens = new List<(string, decimal)>();
        foreach (Match m in NumberToken.Matches(scrubbed))
        {
            var digits = m.Groups[1].Value.Replace(",", "") + m.Groups[2].Value;
            if (!decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                continue;
            if (m.Value.TrimStart().StartsWith('-')) value = -value;
            value *= m.Groups[3].Value.Trim().ToUpperInvariant() switch
            {
                "K" => 1_000m,
                "M" => 1_000_000m,
                "B" => 1_000_000_000m,
                "T" => 1_000_000_000_000m,
                _ => 1m,   // bare or %
            };
            tokens.Add((m.Value.Trim(), value));
        }
        return tokens;
    }

    /// True when the output number equals a source number to 2 significant
    /// figures under any legitimate unit factor.
    public static bool Matches(decimal outputValue, IReadOnlyCollection<decimal> sourceValues)
    {
        foreach (var source in sourceValues)
            foreach (var factor in UnitFactors)
                if (SameToSignificantFigures(outputValue * factor, source)
                    || SameToSignificantFigures(outputValue, source * factor))
                    return true;
        return false;
    }

    private static bool SameToSignificantFigures(decimal a, decimal b)
    {
        if (a == b) return true;
        if (a == 0m || b == 0m) return false;
        if (Math.Sign(a) != Math.Sign(b)) return false;
        return RoundSf(Math.Abs(a)) == RoundSf(Math.Abs(b));
    }

    private static (decimal Significand, int Exponent) RoundSf(decimal value)
    {
        var exponent = (int)Math.Floor(Math.Log10((double)value));
        var scale = (decimal)Math.Pow(10, exponent - (SignificantFigures - 1));
        var significand = Math.Round(value / scale, MidpointRounding.AwayFromZero);
        // rounding can carry (9.96 -> 10); renormalize so 0.0997 matches 0.1
        if (significand >= (decimal)Math.Pow(10, SignificantFigures))
        {
            significand /= 10m;
            exponent += 1;
        }
        return (significand, exponent);
    }

}
