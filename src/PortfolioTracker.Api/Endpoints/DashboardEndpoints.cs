using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Api.Endpoints;

public static class DashboardEndpoints
{
    public record SummaryDto(
        decimal TotalMarketValue, decimal TotalCostBasis, decimal TotalGainLoss, decimal? TotalGainLossPct,
        decimal? DayGainLoss, decimal? DayGainLossPct, bool DayStartIsCustom, int UnpricedCount,
        decimal Deposited, decimal Cash, decimal Dividends, decimal Realized, decimal YtdTaxRealized,
        decimal YtdRealized, decimal YtdWashAdjustment, decimal YtdEstimatedTax,
        decimal AccountTotal, DateTimeOffset? PricesAsOf,
        DateTimeOffset? QuotesObservedAt, bool QuotesStale, bool QuotesViaFallback);

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
        // "YTD Realized (after tax)": wash-adjusted realized minus the estimated
        // federal tax attributable to it, scoped to this dashboard's account.
        // The cumulative view matches the Taxes page's Realized After Tax number;
        // capital-loss carry-in is taxpayer-level so only that view applies it.
        var taxYear = svc.Tax.GetYearData(DateTime.Now.Year, svc.TaxBrackets, accountId);
        // After-tax is ECONOMIC money kept: raw realized − estimated tax. The
        // tax itself is figured on the wash-ADJUSTED (taxable) realized — the
        // disallowed loss add-back raises the tax but is not itself money, so
        // it only appears in the tooltip's explanation, never in the headline.
        // Wash adjustment = taxable − economic so it ties out exactly,
        // absorbed-basis effects included. Same taxable-account scope as the
        // tax events.
        var ytdTaxable = taxYear.YearShortTerm + taxYear.YearLongTerm;
        var ytdEconomic = taxYear.Events.Sum(e => e.View.Source.GainLoss);
        var ytdAfterTax = ytdEconomic - taxYear.Estimate.Attributable;
        var cash = svc.CashFlows.GetTotals(accountId);
        // deposits + dividends + realized P/L == cost basis + uninvested cash
        var accountTotal = cash.Deposits + cash.Dividends + realized;
        var asOf = quotes.Values.Select(q => q.FetchedAt).DefaultIfEmpty().Max();
        // frozen-feed detection: provider answers but keeps serving an old print
        var observedAt = QuoteFreshness.NewestObservation(quotes.Values);
        var quotesStale = QuoteFreshness.IsStale(
            asOf == default ? null : asOf, observedAt, DateTimeOffset.UtcNow);
        // disclose when the latest refresh is riding the Yahoo backup — the
        // numbers are fresh, but the primary feed is down and worth knowing
        var viaFallback = quotes.Values.Any(q =>
            q.Source == "yahoo" && asOf - q.FetchedAt < TimeSpan.FromMinutes(5));

        return new DashboardDto(rows, new SummaryDto(
            s.TotalMarketValue, s.TotalCostBasis, s.TotalGainLoss, s.TotalGainLossPct,
            dayGainLoss, dayGainLossPct, custom is not null, s.UnpricedCount,
            cash.Deposits, accountTotal - s.TotalCostBasis, cash.Dividends, realized,
            ytdAfterTax, ytdEconomic, ytdTaxable - ytdEconomic, taxYear.Estimate.Attributable,
            accountTotal,
            asOf == default ? null : asOf,
            observedAt, quotesStale, viaFallback));
    }
}
