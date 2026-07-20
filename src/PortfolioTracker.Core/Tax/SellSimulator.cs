using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Core.Tax;

public class SimulatedPortion
{
    public required long LotId { get; init; }
    public decimal Shares { get; init; }
    public DateOnly AcquisitionDate { get; init; }       // effective (wash-tacked) date
    public decimal Basis { get; init; }                  // wash-adjusted
    public decimal Proceeds { get; init; }
    public decimal GainLoss => Proceeds - Basis;
    public HoldingTerm Term { get; init; }
    public DateOnly LongTermOn { get; init; }            // first day a sale would be long-term
}

public class DaysToLongTermCallout
{
    public decimal Shares { get; init; }
    public int Days { get; init; }
    public DateOnly Date { get; init; }
    public decimal EstimatedSavings { get; init; }       // (marginal ordinary − LTCG rate) × gain
}

/// What the other accounts did with this ticker in the wash window — the
/// simulator's FIFO preview stays strictly within the selling account, but
/// wash consequences do not.
public class CrossAccountWashContext
{
    public decimal OtherTaxableReplacementShares { get; init; }  // still-held window buys in OTHER taxable accounts
    public decimal IraReplacementShares { get; init; }           // window buys in IRA accounts (Rev. Rul. 2008-5)
    public string? IraWarning { get; init; }                     // pre-built text incl. recurring-pattern detection
}

public class SimulationResult
{
    public List<SimulatedPortion> Portions { get; } = new();
    public decimal ShortTermGainLoss => Portions.Where(p => p.Term == HoldingTerm.ShortTerm).Sum(p => p.GainLoss);
    public decimal LongTermGainLoss => Portions.Where(p => p.Term == HoldingTerm.LongTerm).Sum(p => p.GainLoss);
    public decimal TotalProceeds => Portions.Sum(p => p.Proceeds);
    public decimal EstimatedTaxOnSale { get; set; }      // negative = tax savings
    public List<DaysToLongTermCallout> LongTermCallouts { get; } = new();
    public string? WashWarning { get; set; }             // deferral flavors (same-account / cross-account taxable)
    public string? IraWashWarning { get; set; }          // permanent flavor — show in red
    public decimal SharesRequested { get; init; }
    public decimal SharesAvailable { get; init; }
}

/// "What if I sell?" — previews the exact FIFO consumption of a hypothetical
/// sale with wash-adjusted basis and tacked holding periods, stacks the result
/// on top of YTD realized gains to estimate the federal tax on the sale, and
/// flags wash interactions (all flavors, across all accounts) and short-term
/// lots worth holding for long-term. FIFO consumption is strictly within the
/// selling account. Simulation only: nothing here writes records.
public static class SellSimulator
{
    public static SimulationResult Simulate(
        List<Lot> accountLots, WashSaleResult washState,
        decimal sharesToSell, decimal price, DateOnly saleDate,
        decimal ytdShortTerm, decimal ytdLongTerm,       // wash-adjusted realized so far, incl. carry-in
        YearBrackets brackets, FilingStatus status, decimal otherIncome,
        CrossAccountWashContext? crossAccount = null)
    {
        var openLots = accountLots.Where(l => l.RemainingShares > 0m)
            .OrderBy(l => l.TradeDate).ThenBy(l => l.Id).ToList();
        var available = openLots.Sum(l => l.RemainingShares);
        var result = new SimulationResult
        {
            SharesRequested = sharesToSell,
            SharesAvailable = available,
        };

        var toSell = Math.Min(sharesToSell, available);
        foreach (var lot in openLots)
        {
            if (toSell <= 0m) break;
            var state = washState.OpenLots.GetValueOrDefault(lot.Id);
            var effAcq = state?.EffectiveAcquisitionDate ?? lot.TradeDate;
            var lotBasis = lot.RemainingCost + (state?.BasisAdjustment ?? 0m);
            var shares = Math.Min(lot.RemainingShares, toSell);
            var frac = shares / lot.RemainingShares;
            result.Portions.Add(new SimulatedPortion
            {
                LotId = lot.Id,
                Shares = shares,
                AcquisitionDate = effAcq,
                Basis = lotBasis * frac,
                Proceeds = price * shares,
                Term = ConsumptionDraft.TermFor(effAcq, saleDate),
                LongTermOn = effAcq.AddYears(1).AddDays(1),
            });
            toSell -= shares;
        }

        // tax on the sale = estimate(YTD + sale) − estimate(YTD)
        var before = TaxEstimator.Estimate(brackets, status, otherIncome,
            TaxEstimator.Net(ytdShortTerm, ytdLongTerm));
        var after = TaxEstimator.Estimate(brackets, status, otherIncome,
            TaxEstimator.Net(ytdShortTerm + result.ShortTermGainLoss, ytdLongTerm + result.LongTermGainLoss));
        result.EstimatedTaxOnSale = after.TaxWithInvestments - before.TaxWithInvestments;

        // wash interaction: selling at a loss with replacement purchases in the
        // last 30 days — in this account, another taxable account, or an IRA
        var totalLoss = result.Portions.Where(p => p.GainLoss < 0m).Sum(p => p.GainLoss);
        if (totalLoss < 0m)
        {
            var lossShares = result.Portions.Where(p => p.GainLoss < 0m).Sum(p => p.Shares);
            var soldFromLots = result.Portions.ToDictionary(p => p.LotId, p => p.Shares);
            var sameAccount = accountLots
                .Where(l => l.ParentLotId is null
                            && l.TradeDate >= saleDate.AddDays(-30) && l.TradeDate <= saleDate)
                .Sum(l => RemainingInFamily(accountLots, l) - SoldInSim(accountLots, l, soldFromLots));
            var crossTaxable = crossAccount?.OtherTaxableReplacementShares ?? 0m;
            var ira = crossAccount?.IraReplacementShares ?? 0m;

            var deferralShares = Math.Max(0m, sameAccount) + crossTaxable;
            if (deferralShares > 0m)
            {
                var washedShares = Math.Min(deferralShares, lossShares);
                var disallowed = -totalLoss * (lossShares == 0m ? 0m : washedShares / lossShares);
                result.WashWarning =
                    $"Wash sale: {Trim(washedShares)} of the {Trim(lossShares)} loss shares were repurchased " +
                    $"in the last 30 days — about {disallowed:C2} of this loss would be disallowed " +
                    "(and buying again within 30 days after the sale washes the rest)." +
                    (crossTaxable > 0m
                        ? $" {Trim(Math.Min(crossTaxable, washedShares))} replacement share(s) sit in another taxable " +
                          "account: that portion is a cross-account wash and will NOT appear on either 1099-B."
                        : "");
            }
            else if (ira <= 0m && crossAccount?.IraWarning is null)
            {
                result.WashWarning =
                    "This sale realizes a loss. Buying this ticker back within 30 days — in any account, " +
                    "including an IRA — would wash (disallow) it.";
            }

            // the permanent flavor is always called out separately, in red
            if (ira > 0m || crossAccount?.IraWarning is not null)
            {
                result.IraWashWarning = "IRA wash — loss permanently lost: " + (crossAccount?.IraWarning
                    ?? $"{Trim(Math.Min(ira, lossShares))} replacement share(s) were bought in an IRA within " +
                       "the wash window (Rev. Rul. 2008-5). No basis adjustment anywhere; the deduction is destroyed.");
            }
        }

        // the headline feature: short-term portions that turn long-term soon
        var stackBase = ytdShortTerm + result.ShortTermGainLoss;
        foreach (var p in result.Portions.Where(p => p.Term == HoldingTerm.ShortTerm && p.GainLoss > 0m))
        {
            var std = brackets.StandardDeduction[status];
            var incomePoint = Math.Max(0m, otherIncome - std) + Math.Max(0m, stackBase);
            var ordRate = TaxEstimator.MarginalRate(incomePoint, brackets.OrdinaryBrackets[status]);
            var ltRate = TaxEstimator.MarginalRate(incomePoint, brackets.LtcgBrackets[status]);
            var savings = p.GainLoss * (ordRate - ltRate);
            result.LongTermCallouts.Add(new DaysToLongTermCallout
            {
                Shares = p.Shares,
                Days = p.LongTermOn.DayNumber - saleDate.DayNumber,
                Date = p.LongTermOn,
                EstimatedSavings = savings,
            });
        }
        return result;
    }

    /// Still-held shares from window buys in the given lots, for building the
    /// cross-account context (other taxable accounts vs IRAs).
    public static decimal ReplacementSharesInWindow(List<Lot> lots, DateOnly saleDate)
        => lots.Where(l => l.ParentLotId is null
                           && l.TradeDate >= saleDate.AddDays(-30) && l.TradeDate <= saleDate)
            .Sum(l => RemainingInFamily(lots, l));

    private static decimal RemainingInFamily(List<Lot> lots, Lot root)
    {
        var byId = lots.ToDictionary(l => l.Id);
        return lots.Where(l => RootOf(byId, l).Id == root.Id).Sum(l => l.RemainingShares);
    }

    private static decimal SoldInSim(List<Lot> lots, Lot root, Dictionary<long, decimal> soldFromLots)
    {
        var byId = lots.ToDictionary(l => l.Id);
        return lots.Where(l => RootOf(byId, l).Id == root.Id)
            .Sum(l => soldFromLots.GetValueOrDefault(l.Id));
    }

    private static Lot RootOf(Dictionary<long, Lot> byId, Lot lot)
    {
        var root = lot;
        while (root.ParentLotId is long pid && byId.TryGetValue(pid, out var parent)) root = parent;
        return root;
    }

    private static string Trim(decimal d) => d.ToString("0.#####");
}
