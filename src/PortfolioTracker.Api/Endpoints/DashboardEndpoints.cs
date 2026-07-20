using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Api.Endpoints;

public static class DashboardEndpoints
{
    public record SummaryDto(
        decimal TotalMarketValue, decimal TotalCostBasis, decimal TotalGainLoss, decimal? TotalGainLossPct,
        decimal? DayGainLoss, decimal? DayGainLossPct, bool DayStartIsCustom, int UnpricedCount,
        decimal Deposited, decimal Cash, decimal Dividends, decimal Realized, decimal YtdTaxRealized,
        decimal AccountTotal, DateTimeOffset? PricesAsOf);

    public record DashboardDto(List<PositionRow> Rows, SummaryDto Summary);

    public static void MapDashboardEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/dashboard/cumulative", (CoreServices svc) => Results.Ok(Build(svc, null)));
        api.MapGet("/dashboard/account/{id:long}", (long id, CoreServices svc) =>
            svc.Accounts.GetById(id) is null ? Results.NotFound() : Results.Ok(Build(svc, id)));
    }

    /// One snapshot read shared by both views — the same assembly the WinForms
    /// DashboardControl did. Rows include closed positions (shares == 0); the
    /// frontend applies the show-closed toggle.
    private static DashboardDto Build(CoreServices svc, long? accountId)
    {
        var allPositions = svc.Positions.GetAll();
        var openLots = svc.Lots.GetAllOpen();
        var sales = svc.Sales.GetAll();
        var quotes = svc.PriceCache.GetAll();
        var targets = svc.TickerTargets.GetAll()
            .ToDictionary(t => t.Ticker, t => t, StringComparer.OrdinalIgnoreCase);
        var accounts = svc.Accounts.GetAll().ToDictionary(a => a.Id);
        var unmatched = sales.Where(s => s.UnmatchedShares != 0m)
            .GroupBy(s => s.PositionId)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.UnmatchedShares));
        var realizedByPosition = sales.GroupBy(s => s.PositionId)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.RealizedGainLoss));

        var rows = accountId is long id
            ? PortfolioCalculator.BuildRows(allPositions.Where(p => p.AccountId == id).ToList(),
                openLots, unmatched, quotes, targets, realizedByPosition)
            : PortfolioCalculator.BuildCumulativeRows(allPositions, openLots, unmatched, quotes,
                targets, accounts, realizedByPosition);

        var s = PortfolioCalculator.Summarize(rows);
        // the custom day-start baseline applies to the whole portfolio, so only
        // the cumulative view honors it
        var today = DateOnly.FromDateTime(DateTime.Now);
        var custom = accountId is null && svc.Settings.DayStartDate == today ? svc.Settings.DayStartValue : null;
        var dayGainLoss = custom is decimal baseline ? s.TotalMarketValue - baseline : s.TotalDayGainLoss;
        var dayGainLossPct = custom is decimal b && b > 0m
            ? (s.TotalMarketValue - b) / b * 100m
            : s.TotalDayGainLossPct;

        var realized = (accountId is long a2 ? sales.Where(x => x.AccountId == a2) : sales)
            .Sum(x => x.RealizedGainLoss);
        // "YTD Realized (after tax)": the Taxes page's Realized After Tax number —
        // wash-adjusted realized minus the estimated federal tax attributable to it
        var taxYear = svc.Tax.GetYearData(DateTime.Now.Year, svc.TaxBrackets);
        var ytdAfterTax = taxYear.YearShortTerm + taxYear.YearLongTerm - taxYear.Estimate.Attributable;
        var cash = svc.CashFlows.GetTotals(accountId);
        // deposits + dividends + realized P/L == cost basis + uninvested cash
        var accountTotal = cash.Deposits + cash.Dividends + realized;
        var asOf = quotes.Values.Select(q => q.FetchedAt).DefaultIfEmpty().Max();

        return new DashboardDto(rows, new SummaryDto(
            s.TotalMarketValue, s.TotalCostBasis, s.TotalGainLoss, s.TotalGainLossPct,
            dayGainLoss, dayGainLossPct, custom is not null, s.UnpricedCount,
            cash.Deposits, accountTotal - s.TotalCostBasis, cash.Dividends, realized,
            ytdAfterTax, accountTotal,
            asOf == default ? null : asOf));
    }
}
