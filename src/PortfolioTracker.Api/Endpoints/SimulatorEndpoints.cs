using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Tax;

namespace PortfolioTracker.Api.Endpoints;

public static class SimulatorEndpoints
{
    public record SellRequest(long AccountId, string Ticker, decimal Shares, decimal Price);

    public record PortionDto(DateOnly Acquired, decimal Shares, HoldingTerm Term,
        decimal Basis, decimal Proceeds, decimal GainLoss, DateOnly LongTermOn);

    public record CalloutDto(decimal Shares, int Days, DateOnly Date, decimal EstimatedSavings);

    public record SimulationDto(List<PortionDto> Portions,
        decimal ShortTermGainLoss, decimal LongTermGainLoss, decimal TotalProceeds,
        decimal EstimatedTaxOnSale, string? WashWarning, string? IraWashWarning,
        List<CalloutDto> LongTermCallouts, decimal SharesRequested, decimal SharesAvailable);

    public static void MapSimulatorEndpoints(this IEndpointRouteBuilder api)
    {
        // "What if I sell?" — the SellSimulatorForm computation, verbatim.
        // Simulation only: nothing here writes records.
        api.MapPost("/simulator/sell", (SellRequest req, CoreServices svc) =>
        {
            if (req.Shares <= 0m) return Results.BadRequest(new { error = "shares must be a positive number" });
            if (req.Price <= 0m) return Results.BadRequest(new { error = "price must be a positive number" });
            var account = svc.Accounts.GetById(req.AccountId);
            if (account is null) return Results.NotFound(new { error = $"account {req.AccountId} does not exist" });
            var ticker = req.Ticker.Trim().ToUpperInvariant();
            var pos = svc.Positions.GetByAccountAndTicker(account.Id, ticker);
            if (pos is null) return Results.NotFound(new { error = $"no {ticker} position in {account.DisplayName}" });

            var accountLots = svc.Lots.GetByPosition(pos.Id, openOnly: false);
            var today = DateOnly.FromDateTime(DateTime.Now);
            var year = today.Year;
            var ytd = svc.Tax.GetYearData(year, svc.TaxBrackets);
            var brackets = svc.TaxBrackets.ForYear(year);

            // wash checks span all accounts: other-taxable window buys defer the
            // loss; IRA window buys destroy it permanently (Rev. Rul. 2008-5)
            var accountsById = svc.Accounts.GetAll().ToDictionary(a => a.Id);
            decimal otherTaxable = 0m, ira = 0m;
            foreach (var other in svc.Positions.GetAllByTicker(ticker).Where(p => p.Id != pos.Id))
            {
                if (!accountsById.TryGetValue(other.AccountId, out var otherAccount)) continue;
                var replacement = SellSimulator.ReplacementSharesInWindow(
                    svc.Lots.GetByPosition(other.Id, openOnly: false), today);
                if (otherAccount.IsRetirement) ira += replacement;
                else otherTaxable += replacement;
            }
            var iraRisk = svc.Tax.GetIraWashRisk(ticker, today, account.DisplayName);
            var context = new CrossAccountWashContext
            {
                OtherTaxableReplacementShares = otherTaxable,
                IraReplacementShares = ira,
                IraWarning = iraRisk?.Warning,
            };

            var sim = SellSimulator.Simulate(accountLots, svc.Tax.ComputeForTicker(ticker),
                req.Shares, req.Price, today,
                ytd.Netting.NetShortTerm, ytd.Netting.NetLongTerm,
                brackets, svc.Tax.Settings.GetFilingStatus(), svc.Tax.Settings.GetOtherIncome(),
                context);

            return Results.Ok(new SimulationDto(
                sim.Portions.Select(p => new PortionDto(p.AcquisitionDate, p.Shares, p.Term,
                    p.Basis, p.Proceeds, p.GainLoss, p.LongTermOn)).ToList(),
                sim.ShortTermGainLoss, sim.LongTermGainLoss, sim.TotalProceeds,
                sim.EstimatedTaxOnSale, sim.WashWarning, sim.IraWashWarning,
                sim.LongTermCallouts.Select(c => new CalloutDto(c.Shares, c.Days, c.Date, c.EstimatedSavings)).ToList(),
                sim.SharesRequested, sim.SharesAvailable));
        });
    }
}
