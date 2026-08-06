using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Tax;

namespace PortfolioTracker.Api.Endpoints;

public static class TaxEndpoints
{
    public record TaxEventDto(string Ticker, string Account, long AccountId,
        DateOnly Acquired, DateOnly Sold, int DaysHeld, HoldingTerm Term, decimal Shares,
        decimal Proceeds, decimal Basis, decimal GainLoss,
        bool IsWash, decimal Disallowed, decimal CrossAccountDisallowed, decimal IraDisallowed,
        bool HasCrossAccountWash, bool HasIraWash);

    public record NettingDto(decimal NetShortTerm, decimal NetLongTerm,
        decimal TaxableShortTerm, decimal TaxableLongTerm, decimal OrdinaryOffset,
        decimal CarryforwardShortTerm, decimal CarryforwardLongTerm);

    public record EstimateDto(decimal TaxWithInvestments, decimal TaxWithoutInvestments, decimal Attributable);

    public record DividendDto(string Ticker, decimal Amount);

    public record TaxYearDto(int Year, List<int> AvailableYears, List<TaxEventDto> Events,
        decimal YearRealized,                       // raw (pre-wash) realized, for economic after-tax
        decimal YearShortTerm, decimal YearLongTerm,
        decimal CarryInShortTerm, decimal CarryInLongTerm, bool CarryInIsManual,
        NettingDto Netting, decimal TotalDisallowed, decimal PermanentlyLost,
        decimal GrossDividends, List<DividendDto> DividendsByTicker,
        EstimateDto Estimate, bool BracketsAreFallback, int RetirementAccountsExcluded);

    public record WashRiskDto(string Ticker, decimal LossAtRisk, DateOnly LatestSale,
        DateOnly SafeToBuyOn, int DaysToWait);

    public record CarryforwardPatch(decimal ShortTerm, decimal LongTerm);

    public static void MapTaxEndpoints(this IEndpointRouteBuilder api)
    {
        // the TaxesForm dashboard payload: summary, netting, events, dividends, estimate
        api.MapGet("/tax/{year:int}", (int year, CoreServices svc) =>
        {
            var data = svc.Tax.GetYearData(year, svc.TaxBrackets);
            // year selector mirrors WinForms: every event year plus the current year
            var years = svc.Tax.GetAllEvents().Select(e => e.View.Source.SaleTradeDate.Year)
                .Append(DateTime.Now.Year).Distinct().OrderByDescending(y => y).ToList();
            return Results.Ok(ToDto(data, years));
        });

        // Form 8949-style CSV, byte-for-byte the WinForms export
        api.MapGet("/tax/{year:int}/8949.csv", (int year, CoreServices svc) =>
        {
            var data = svc.Tax.GetYearData(year, svc.TaxBrackets);
            var csv = Form8949Export.BuildCsv(data.Year, data.Events);
            return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", $"form8949-{year}.csv");
        });

        // {year} here is the year the loss arose (carried INTO year+1) — the
        // frontend edits the carry-in of Y by patching Y-1, like CarryforwardForm
        api.MapPatch("/tax/{year:int}/carryforward", (int year, CarryforwardPatch patch, CoreServices svc) =>
        {
            if (patch.ShortTerm < 0m || patch.LongTerm < 0m)
                return Results.BadRequest(new { error = "carryforward amounts must be non-negative" });
            svc.Tax.SetManualCarryforward(year, patch.ShortTerm, patch.LongTerm);
            return Results.NoContent();
        });

        api.MapDelete("/tax/{year:int}/carryforward", (int year, CoreServices svc) =>
        {
            svc.Tax.ClearManualCarryforward(year);
            return Results.NoContent();
        });

        // open wash windows: tickers not to rebuy right now
        api.MapGet("/tax/washrisks", (CoreServices svc) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            return Results.Ok(svc.Tax.GetActiveWashRisks(today)
                .Select(r => new WashRiskDto(r.Ticker, r.LossAtRisk, r.LatestSale, r.SafeToBuyOn,
                    Math.Max(0, r.SafeToBuyOn.DayNumber - today.DayNumber))));
        });
    }

    private static TaxYearDto ToDto(TaxYearData d, List<int> years) => new(
        d.Year, years,
        d.Events.Select(e => new TaxEventDto(
            e.Ticker, e.Account, e.AccountId,
            e.View.Source.LotAcquisitionDate, e.View.Source.SaleTradeDate,
            e.View.Source.HoldingDays, e.View.EffectiveTerm, e.View.Source.SharesConsumed,
            e.View.Source.Proceeds, e.View.AdjustedBasis, e.View.AdjustedGainLoss,
            e.View.IsWash, e.View.DisallowedLoss, e.View.CrossAccountDisallowed, e.View.IraDisallowed,
            e.View.HasCrossAccountWash, e.View.HasIraWash)).ToList(),
        d.Events.Sum(e => e.View.Source.GainLoss),
        d.YearShortTerm, d.YearLongTerm,
        d.CarryInShortTerm, d.CarryInLongTerm, d.CarryInIsManual,
        new NettingDto(d.Netting.NetShortTerm, d.Netting.NetLongTerm,
            d.Netting.TaxableShortTerm, d.Netting.TaxableLongTerm, d.Netting.OrdinaryOffset,
            d.Netting.CarryforwardShortTerm, d.Netting.CarryforwardLongTerm),
        d.TotalDisallowed, d.PermanentlyLost,
        d.GrossDividends, d.DividendsByTicker.Select(x => new DividendDto(x.Ticker, x.Amount)).ToList(),
        new EstimateDto(d.Estimate.TaxWithInvestments, d.Estimate.TaxWithoutInvestments, d.Estimate.Attributable),
        d.BracketsAreFallback, d.RetirementAccountsExcluded);
}
