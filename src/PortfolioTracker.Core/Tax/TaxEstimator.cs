using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tax;

/// ST and LT results after netting, the up-to-$3,000 ordinary income offset,
/// and what carries forward (stored positive).
public record NettingResult(
    decimal NetShortTerm, decimal NetLongTerm,           // year nets including carry-in
    decimal TaxableShortTerm, decimal TaxableLongTerm,   // after cross-netting; never negative
    decimal OrdinaryOffset,                              // 0..3000 applied against ordinary income
    decimal CarryforwardShortTerm, decimal CarryforwardLongTerm);

public record TaxEstimate(decimal TaxWithInvestments, decimal TaxWithoutInvestments)
{
    public decimal Attributable => TaxWithInvestments - TaxWithoutInvestments;
}

/// Simplified federal model, all decimal, strictly marginal:
///   taxable income = other income − standard deduction
///   net ST gains stack on top as ordinary income
///   net LT gains stack above that into the LTCG brackets
/// Ignores NIIT, AMT, credits, and the qualified-dividend split.
public static class TaxEstimator
{
    public const decimal OrdinaryLossOffsetCap = 3_000m;

    /// ST nets against ST, LT against LT, then cross-net; a remaining net loss
    /// offsets up to $3,000 of ordinary income (ST loss used first) and the rest
    /// carries forward keeping its character.
    public static NettingResult Net(decimal netShortTerm, decimal netLongTerm)
    {
        decimal taxableSt = 0m, taxableLt = 0m, offset = 0m, carrySt = 0m, carryLt = 0m;
        if (netShortTerm >= 0m && netLongTerm >= 0m)
        {
            taxableSt = netShortTerm; taxableLt = netLongTerm;
        }
        else if (netShortTerm < 0m && netLongTerm >= 0m)
        {
            var crossed = netLongTerm + netShortTerm;      // ST loss absorbs LT gain
            if (crossed >= 0m) taxableLt = crossed;
            else (offset, carrySt) = SplitLoss(-crossed);  // leftover loss keeps ST character
        }
        else if (netShortTerm >= 0m && netLongTerm < 0m)
        {
            var crossed = netShortTerm + netLongTerm;      // LT loss absorbs ST gain
            if (crossed >= 0m) taxableSt = crossed;
            else (offset, carryLt) = SplitLoss(-crossed);  // leftover loss keeps LT character
        }
        else
        {
            // both losses: the $3,000 offset uses ST first, both characters carry
            var offsetSt = Math.Min(OrdinaryLossOffsetCap, -netShortTerm);
            var offsetLt = Math.Min(OrdinaryLossOffsetCap - offsetSt, -netLongTerm);
            offset = offsetSt + offsetLt;
            carrySt = -netShortTerm - offsetSt;
            carryLt = -netLongTerm - offsetLt;
        }
        return new NettingResult(netShortTerm, netLongTerm, taxableSt, taxableLt, offset, carrySt, carryLt);

        static (decimal offset, decimal carry) SplitLoss(decimal loss) =>
            (Math.Min(OrdinaryLossOffsetCap, loss), Math.Max(0m, loss - OrdinaryLossOffsetCap));
    }

    public static TaxEstimate Estimate(YearBrackets brackets, FilingStatus status,
        decimal otherIncome, NettingResult netting)
    {
        var std = brackets.StandardDeduction[status];
        var ordinary = brackets.OrdinaryBrackets[status];
        var ltcg = brackets.LtcgBrackets[status];

        var baseTaxable = Math.Max(0m, otherIncome - std);
        var without = TaxOnSpan(0m, baseTaxable, ordinary);

        var offsetBase = Math.Max(0m, otherIncome - netting.OrdinaryOffset - std);
        var ordinaryTaxable = offsetBase + netting.TaxableShortTerm;
        var with = TaxOnSpan(0m, ordinaryTaxable, ordinary)
                 + TaxOnSpan(ordinaryTaxable, ordinaryTaxable + netting.TaxableLongTerm, ltcg);
        return new TaxEstimate(with, without);
    }

    /// Marginal tax on the income span [from, to] against tiered brackets.
    public static decimal TaxOnSpan(decimal from, decimal to, List<BracketTier> tiers)
    {
        if (to <= from) return 0m;
        decimal tax = 0m, floor = 0m;
        foreach (var tier in tiers)
        {
            var ceiling = tier.UpTo ?? decimal.MaxValue;
            var lo = Math.Max(from, floor);
            var hi = Math.Min(to, ceiling);
            if (hi > lo) tax += (hi - lo) * tier.Rate;
            floor = ceiling;
            if (floor >= to) break;
        }
        return tax;
    }

    /// The rate that would apply to the next dollar at this income point.
    public static decimal MarginalRate(decimal at, List<BracketTier> tiers)
    {
        foreach (var tier in tiers)
            if (tier.UpTo is not decimal upTo || at < upTo) return tier.Rate;
        return tiers[^1].Rate;
    }
}
