using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Portfolio;

public class PositionRow
{
    public long PositionId { get; set; }             // 0 on cumulative rows
    public long AccountId { get; set; }              // 0 on cumulative rows
    public string Ticker { get; set; } = "";
    public decimal Shares { get; set; }
    public decimal CostBasis { get; set; }
    public decimal? AvgCost { get; set; }            // cumulative: blended = total cost / total shares
    public decimal? CurrentPrice { get; set; }
    public DateTimeOffset? PriceAsOf { get; set; }
    public decimal? MarketValue { get; set; }
    public decimal? GainLoss { get; set; }
    public decimal? GainLossPct { get; set; }
    public decimal? DayGainLoss { get; set; }        // null when the quote has no previous close
    public decimal? RealizedGainLoss { get; set; }   // lifetime, from sales; null when never sold
    public decimal? TargetPrice { get; set; }        // ticker-level (TickerTarget)
    public TargetDirection TargetDirection { get; set; }
    public bool AlertEnabled { get; set; }
    public DateTimeOffset? AlertTriggeredAt { get; set; }
    public decimal? DistanceToTargetPct { get; set; }
    public bool NeedsOpeningBalance { get; set; }
    public bool PriceUnavailable { get; set; }
    public bool HeldInIra { get; set; }              // cumulative glyph: some shares sit in an IRA
    public string? IraNote { get; set; }             // tooltip, e.g. "Also held in Roth IRA ...5678"
}

public class PortfolioSummary
{
    public decimal TotalMarketValue { get; set; }
    public decimal TotalCostBasis { get; set; }
    public decimal TotalGainLoss { get; set; }
    public decimal? TotalGainLossPct { get; set; }
    public decimal? TotalDayGainLoss { get; set; }
    public decimal? TotalDayGainLossPct { get; set; }
    public int UnpricedCount { get; set; }
}

public static class PortfolioCalculator
{
    /// One row per position (one account's view, or every account's rows when
    /// the caller doesn't filter). Targets come from ticker level.
    public static List<PositionRow> BuildRows(List<Position> positions, List<Lot> allOpenLots,
        Dictionary<long, decimal> unmatchedByPosition, IReadOnlyDictionary<string, PriceQuote> quotes,
        IReadOnlyDictionary<string, TickerTarget> targets,
        Dictionary<long, decimal>? realizedByPosition = null)
    {
        var lotsByPosition = allOpenLots.ToLookup(l => l.PositionId);
        var rows = new List<PositionRow>(positions.Count);
        foreach (var p in positions)
        {
            var open = lotsByPosition[p.Id].ToList();
            var shares = open.Sum(l => l.RemainingShares) - unmatchedByPosition.GetValueOrDefault(p.Id);
            var costBasis = open.Sum(l => l.RemainingCost);
            var row = new PositionRow
            {
                PositionId = p.Id, AccountId = p.AccountId, Ticker = p.Ticker,
                Shares = shares, CostBasis = costBasis,
                AvgCost = shares > 0m ? costBasis / shares : null,
                RealizedGainLoss = realizedByPosition is not null
                    && realizedByPosition.TryGetValue(p.Id, out var realized) ? realized : null,
                NeedsOpeningBalance = p.NeedsOpeningBalance,
            };
            ApplyTargetAndQuote(row, targets, quotes);
            rows.Add(row);
        }
        return rows;
    }

    /// The "All accounts" tab: one row per ticker aggregated across accounts.
    /// Shares and cost are sums, avg cost is blended, market value and G/L come
    /// from the aggregates.
    public static List<PositionRow> BuildCumulativeRows(List<Position> positions, List<Lot> allOpenLots,
        Dictionary<long, decimal> unmatchedByPosition, IReadOnlyDictionary<string, PriceQuote> quotes,
        IReadOnlyDictionary<string, TickerTarget> targets, IReadOnlyDictionary<long, Account> accounts,
        Dictionary<long, decimal>? realizedByPosition = null)
    {
        var lotsByPosition = allOpenLots.ToLookup(l => l.PositionId);
        var rows = new List<PositionRow>();
        foreach (var group in positions.GroupBy(p => p.Ticker, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            decimal shares = 0m, costBasis = 0m;
            decimal? realizedSum = null;
            var iraHoldings = new List<string>();
            foreach (var p in group)
            {
                var open = lotsByPosition[p.Id].ToList();
                var posShares = open.Sum(l => l.RemainingShares) - unmatchedByPosition.GetValueOrDefault(p.Id);
                shares += posShares;
                costBasis += open.Sum(l => l.RemainingCost);
                if (realizedByPosition is not null && realizedByPosition.TryGetValue(p.Id, out var realized))
                    realizedSum = (realizedSum ?? 0m) + realized;
                if (posShares != 0m && accounts.TryGetValue(p.AccountId, out var acct) && acct.IsRetirement)
                    iraHoldings.Add($"{TreatmentLabel(acct.TaxTreatment)} {acct.AccountNumber}".Trim());
            }
            var row = new PositionRow
            {
                Ticker = group.First().Ticker,
                Shares = shares, CostBasis = costBasis,
                AvgCost = shares > 0m ? costBasis / shares : null,
                RealizedGainLoss = realizedSum,
                NeedsOpeningBalance = group.Any(p => p.NeedsOpeningBalance),
                HeldInIra = iraHoldings.Count > 0,
                IraNote = iraHoldings.Count == 0 ? null : $"Also held in {string.Join(", ", iraHoldings)}",
            };
            ApplyTargetAndQuote(row, targets, quotes);
            rows.Add(row);
        }
        return rows;
    }

    private static void ApplyTargetAndQuote(PositionRow row,
        IReadOnlyDictionary<string, TickerTarget> targets, IReadOnlyDictionary<string, PriceQuote> quotes)
    {
        if (targets.TryGetValue(row.Ticker, out var t))
        {
            row.TargetPrice = t.TargetPrice;
            row.TargetDirection = t.TargetDirection;
            row.AlertEnabled = t.AlertEnabled;
            row.AlertTriggeredAt = t.AlertTriggeredAt;
        }
        if (quotes.TryGetValue(row.Ticker, out var q))
        {
            row.CurrentPrice = q.Price;
            row.PriceAsOf = q.FetchedAt;
            row.MarketValue = row.Shares * q.Price;
            row.GainLoss = row.MarketValue - row.CostBasis;
            row.GainLossPct = row.CostBasis > 0m ? row.GainLoss / row.CostBasis * 100m : null;
            if (q.PrevClose is decimal pc)
                row.DayGainLoss = row.Shares * (q.Price - pc);
            if (row.TargetPrice is decimal target && q.Price > 0m)
                row.DistanceToTargetPct = (target - q.Price) / q.Price * 100m;
        }
        else row.PriceUnavailable = true;
    }

    public static string TreatmentLabel(TaxTreatment t) => t switch
    {
        TaxTreatment.RothIRA => "Roth IRA",
        TaxTreatment.TraditionalIRA => "Traditional IRA",
        _ => "Taxable",
    };

    public static PortfolioSummary Summarize(IEnumerable<PositionRow> rows)
    {
        var open = rows.Where(r => r.Shares != 0m).ToList();
        var priced = open.Where(r => r.MarketValue is not null).ToList();
        var pricedCost = priced.Sum(r => r.CostBasis);
        var withDay = priced.Where(r => r.DayGainLoss is not null).ToList();
        var dayGainLoss = withDay.Count > 0 ? withDay.Sum(r => r.DayGainLoss!.Value) : (decimal?)null;
        // yesterday's value of the same holdings, for the day-change percentage
        var prevValue = withDay.Sum(r => r.MarketValue!.Value - r.DayGainLoss!.Value);
        return new PortfolioSummary
        {
            TotalMarketValue = priced.Sum(r => r.MarketValue!.Value),
            TotalCostBasis = open.Sum(r => r.CostBasis),
            TotalGainLoss = priced.Sum(r => r.GainLoss!.Value),
            TotalGainLossPct = pricedCost > 0m ? priced.Sum(r => r.GainLoss!.Value) / pricedCost * 100m : null,
            TotalDayGainLoss = dayGainLoss,
            TotalDayGainLossPct = dayGainLoss is not null && prevValue > 0m ? dayGainLoss / prevValue * 100m : null,
            UnpricedCount = open.Count(r => r.PriceUnavailable),
        };
    }
}
