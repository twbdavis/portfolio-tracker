using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Tax;

namespace PortfolioTracker.Core.Tests;

public class TaxEstimatorTests
{
    private static readonly YearBrackets B2026 = TaxBracketsFile.Seed2026().Years[2026];

    [Fact]
    public void Ordinary_tax_is_marginal_not_flat()
    {
        var tiers = B2026.OrdinaryBrackets[FilingStatus.Single];
        // 12,400 * 10% + (50,000 - 12,400) * 12%
        Assert.Equal(1_240m + 4_512m, TaxEstimator.TaxOnSpan(0m, 50_000m, tiers));
        // a span that crosses the 12% -> 22% boundary at 50,400
        Assert.Equal(400m * 0.12m + 9_600m * 0.22m, TaxEstimator.TaxOnSpan(50_000m, 60_000m, tiers));
    }

    [Fact]
    public void Ltcg_stacks_on_top_of_ordinary_income()
    {
        var lt = B2026.LtcgBrackets[FilingStatus.Single];
        // 20k LT gain on top of 40k ordinary: 9,450 in the 0% tier, 10,550 at 15%
        Assert.Equal(10_550m * 0.15m, TaxEstimator.TaxOnSpan(40_000m, 60_000m, lt));
        // fully inside the 0% tier
        Assert.Equal(0m, TaxEstimator.TaxOnSpan(10_000m, 30_000m, lt));
    }

    [Fact]
    public void Netting_st_loss_absorbs_lt_gain_and_leftover_keeps_st_character()
    {
        var n = TaxEstimator.Net(-8_000m, 3_000m);
        Assert.Equal(0m, n.TaxableShortTerm);
        Assert.Equal(0m, n.TaxableLongTerm);
        Assert.Equal(3_000m, n.OrdinaryOffset);
        Assert.Equal(2_000m, n.CarryforwardShortTerm);   // 5,000 leftover minus 3,000 offset
        Assert.Equal(0m, n.CarryforwardLongTerm);
    }

    [Fact]
    public void Netting_lt_loss_absorbs_st_gain()
    {
        var n = TaxEstimator.Net(5_000m, -2_000m);
        Assert.Equal(3_000m, n.TaxableShortTerm);
        Assert.Equal(0m, n.TaxableLongTerm);
        Assert.Equal(0m, n.OrdinaryOffset);
    }

    [Fact]
    public void Netting_both_losses_uses_st_first_for_the_offset_and_carries_by_character()
    {
        var n = TaxEstimator.Net(-2_000m, -4_000m);
        Assert.Equal(3_000m, n.OrdinaryOffset);          // 2,000 ST + 1,000 LT
        Assert.Equal(0m, n.CarryforwardShortTerm);
        Assert.Equal(3_000m, n.CarryforwardLongTerm);
    }

    [Fact]
    public void Netting_small_loss_offsets_fully_with_no_carryforward()
    {
        var n = TaxEstimator.Net(-1_200m, 0m);
        Assert.Equal(1_200m, n.OrdinaryOffset);
        Assert.Equal(0m, n.CarryforwardShortTerm);
    }

    [Fact]
    public void Estimate_attributable_tax_stacks_st_as_ordinary_and_lt_above()
    {
        // Single, 66,100 other income -> 50,000 taxable after the 16,100 deduction
        var n = TaxEstimator.Net(10_000m, 20_000m);
        var e = TaxEstimator.Estimate(B2026, FilingStatus.Single, 66_100m, n);
        var baseTax = 1_240m + 37_600m * 0.12m;                        // tax on 50,000
        var stTax = 400m * 0.12m + 9_600m * 0.22m;                     // 50,000 -> 60,000
        var ltTax = 20_000m * 0.15m;                                   // stacked above 60,000 (> 49,450)
        Assert.Equal(baseTax, e.TaxWithoutInvestments);
        Assert.Equal(baseTax + stTax + ltTax, e.TaxWithInvestments);
        Assert.Equal(stTax + ltTax, e.Attributable);
    }

    [Fact]
    public void Estimate_net_loss_reduces_ordinary_income_and_tax()
    {
        var n = TaxEstimator.Net(-5_000m, 0m);
        var e = TaxEstimator.Estimate(B2026, FilingStatus.Single, 66_100m, n);
        Assert.True(e.Attributable < 0m);                              // the $3,000 offset saves tax
        Assert.Equal(-3_000m * 0.12m, e.Attributable);                 // offset lands in the 12% bracket
    }

    [Fact]
    public void Marginal_rate_lookup()
    {
        var tiers = B2026.OrdinaryBrackets[FilingStatus.Single];
        Assert.Equal(0.10m, TaxEstimator.MarginalRate(0m, tiers));
        Assert.Equal(0.12m, TaxEstimator.MarginalRate(12_400m, tiers));  // first dollar of the next tier
        Assert.Equal(0.22m, TaxEstimator.MarginalRate(70_000m, tiers));
        Assert.Equal(0.37m, TaxEstimator.MarginalRate(1_000_000m, tiers));
    }

    [Fact]
    public void Brackets_file_seeds_and_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pt-brackets-{Guid.NewGuid():N}.json");
        try
        {
            var seeded = TaxBracketsFile.Load(path);                   // creates the file
            Assert.True(File.Exists(path));
            Assert.Equal(16_100m, seeded.Years[2026].StandardDeduction[FilingStatus.Single]);
            var reloaded = TaxBracketsFile.Load(path);                 // reads it back
            Assert.Equal(49_450m, reloaded.Years[2026].LtcgBrackets[FilingStatus.Single][0].UpTo);
            Assert.False(reloaded.HasYear(2027));
            Assert.Same(reloaded.Years[2026], reloaded.ForYear(2027)); // falls back to nearest earlier year
        }
        finally { File.Delete(path); }
    }
}
