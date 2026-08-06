using PortfolioTracker.Core.Research;

namespace PortfolioTracker.Core.Tests;

public class GroundingCheckerTests
{
    private const string Slice = """
        {
          "ticker": "MU",
          "marketCapMillions": 120000,
          "pe": 12.4,
          "revenueGrowthYoY": 21.04,
          "netMargin": 0.62,
          "loss": -3.2,
          "shares": 1234,
          "correlation": 0.21
        }
        """;

    private static GroundingResult Check(string output, string template = "") =>
        GroundingChecker.Check(output, Slice, template);

    // ---- number matching ---------------------------------------------------------

    [Fact]
    public void Numbers_present_in_the_slice_pass()
    {
        var result = Check("P/E of 12.4 with revenue growth of 21.0% and correlation 0.21.");
        Assert.True(result.Passed, string.Join("; ", result.Violations.Select(v => v.Detail)));
    }

    [Fact]
    public void Invented_numbers_are_flagged()
    {
        var result = Check("P/E of 12.4 and an invented operating margin of 47.9%.");
        Assert.False(result.Passed);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(GroundingViolation.Number, violation.Kind);
        Assert.Contains("47.9", violation.Detail);
    }

    [Fact]
    public void Billion_suffix_matches_a_value_stored_in_millions()
    {
        Assert.True(Check("A market cap around $120B.").Passed);
        Assert.True(Check("A market cap of $120,000M.").Passed);
        Assert.False(Check("A market cap around $310B.").Passed);
    }

    [Fact]
    public void Percent_and_decimal_forms_are_interchangeable()
    {
        Assert.True(Check("Net margin of 62%.").Passed);          // slice holds 0.62
        Assert.True(Check("Net margin of 0.62.").Passed);
        Assert.True(Check("Growth around 21%.").Passed);          // 21.04 to 2 sig figs
    }

    [Fact]
    public void Negative_numbers_match_only_negatives()
    {
        Assert.True(Check("A drawdown of -3.2% last month.").Passed);
        Assert.False(Check("A gain of 3.7% last month.").Passed);
    }

    [Fact]
    public void Comma_formatted_numbers_parse()
    {
        Assert.True(Check("Share count of 1,234.").Passed);
        Assert.True(Check("About 1,200 shares.").Passed);   // 2 sig figs of 1234
    }

    [Fact]
    public void Dates_are_excluded_from_matching()
    {
        // none of these numbers exist in the slice, but they're all date-shaped
        var result = Check("Reported on 2026-08-04 (Q3), IPO in 1984, next report 8/15/2026, " +
                           "near its 52-week high.");
        Assert.True(result.Passed, string.Join("; ", result.Violations.Select(v => v.Detail)));
    }

    [Fact]
    public void Month_name_dates_and_sec_citations_are_excluded_from_matching()
    {
        // HAS failed on "July 20, 2026" (the 20) and "10b5-1 plans" (the 10)
        var result = Check("A Death Cross hit on July 20, 2026; the Jan 5th sales were part " +
                           "of pre-scheduled 10b5-1 plans.");
        Assert.True(result.Passed, string.Join("; ", result.Violations.Select(v => v.Detail)));
        // lowercase "may" before a number is prose, not a date — still checked
        Assert.False(Check("Margins may 47.9% improve.").Passed);
    }

    [Fact]
    public void Ordered_list_markers_are_structure_not_data()
    {
        // the questions section is REQUIRED to be a numbered list; its markers
        // must never need to exist in the data (TEL failed twice on the "4.")
        var result = Check("1. First question about the 12.4 P/E?\n" +
                           "2. Second question?\n3. Third?\n4. Fourth?\n5. Fifth?");
        Assert.True(result.Passed, string.Join("; ", result.Violations.Select(v => v.Detail)));
        // a data number at a line start followed by a word is still checked
        Assert.False(Check("47.9 was the operating margin.").Passed);
    }

    [Fact]
    public void Score_out_of_100_is_judgment_not_data()
    {
        // the Buy score is the model's own verdict by design — "64/100" must
        // never need to exist in the payload
        var result = Check("**Score: 64/100**\n- Grounded in the 12.4 P/E and 21.04% growth.");
        Assert.True(result.Passed, string.Join("; ", result.Violations.Select(v => v.Detail)));
    }

    [Fact]
    public void Template_declared_literals_are_whitelisted()
    {
        var template = "Format contract: exactly 3 bullets, each under 40 words.";
        var flagged = GroundingChecker.Check("Here are 3 things in under 40 words.", Slice, "");
        Assert.False(flagged.Passed);
        var allowed = GroundingChecker.Check("Here are 3 things in under 40 words.", Slice, template);
        Assert.True(allowed.Passed);
    }

    [Fact]
    public void Rounding_to_two_significant_figures_tolerates_reformatting()
    {
        Assert.True(Check("Revenue grew about 21% year over year.").Passed);   // 21.04 -> 21
        Assert.False(Check("Revenue grew about 25% year over year.").Passed);  // materially wrong
    }

    // ---- vocabulary is not policed -----------------------------------------------------

    [Theory]
    [InlineData("Analyst counts: 24 Buy, 13 Hold, 2 Sell.")]
    [InlineData("The analyst buy ratio rose to 0.62 across three months.")]
    [InlineData("This reads like a classic value setup, cheap relative to peers.")]
    public void Word_choice_is_never_flagged_only_numbers_are(string output)
    {
        // the checker's one job is numbers; word choice (including buy/hold/
        // sell rating labels straight from the payload) is the reader's call
        var result = Check(output);
        Assert.DoesNotContain(result.Violations, v => v.Kind != GroundingViolation.Number);
    }
}
