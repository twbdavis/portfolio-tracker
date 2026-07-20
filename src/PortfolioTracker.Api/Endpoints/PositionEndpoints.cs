using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Api.Endpoints;

public static class PositionEndpoints
{
    public record LotDto(long Id, decimal Shares, decimal TotalCost, decimal RemainingShares,
        decimal RemainingCost, DateOnly TradeDate, LotSource Source, long? SourceTxId,
        DateOnly LongTermOn, int? DaysToLongTerm);

    public record SaleDto(long Id, DateOnly TradeDate, decimal SharesSold, decimal Proceeds,
        decimal CostBasisConsumed, decimal RealizedGainLoss, decimal UnmatchedShares);

    public record PositionAccountDto(long PositionId, long AccountId, string Account, string TreatmentLabel,
        bool IsRetirement, bool NeedsOpeningBalance, bool HasImportedTransactions,
        List<LotDto> Lots, List<SaleDto> Sales);

    public record PositionDetailDto(string Ticker, string? Cusip, string? Name, List<PositionAccountDto> Accounts);

    public record AddLotRequest(long AccountId, string Ticker, decimal Shares, decimal TotalCost,
        DateOnly TradeDate, bool OpeningBalance);
    public record UpdateLotRequest(decimal Shares, decimal TotalCost, DateOnly TradeDate);

    public static void MapPositionEndpoints(this IEndpointRouteBuilder api)
    {
        // per-ticker detail across every account holding it; account tabs filter client-side
        api.MapGet("/positions/{ticker}/detail", (string ticker, CoreServices svc) =>
        {
            var positions = svc.Positions.GetAllByTicker(ticker);
            if (positions.Count == 0) return Results.NotFound();
            var accounts = svc.Accounts.GetAll().ToDictionary(a => a.Id);
            var today = DateOnly.FromDateTime(DateTime.Now);

            var details = positions.Select(p =>
            {
                var account = accounts.GetValueOrDefault(p.AccountId);
                var lots = svc.Lots.GetByPosition(p.Id, openOnly: false)
                    .Select(l => ToLotDto(l, today)).ToList();
                var sales = svc.Sales.GetByPosition(p.Id)
                    .Select(s => new SaleDto(s.Id, s.TradeDate, s.SharesSold, s.Proceeds,
                        s.CostBasisConsumed, s.RealizedGainLoss, s.UnmatchedShares)).ToList();
                var hasImported = svc.Transactions.GetByAccountAndTicker(p.AccountId, p.Ticker)
                    .Any(t => t.Source == LotSource.ChaseImport);
                return new PositionAccountDto(p.Id, p.AccountId,
                    account?.DisplayName ?? "?", PortfolioCalculator.TreatmentLabel(account?.TaxTreatment ?? TaxTreatment.Taxable),
                    account?.IsRetirement ?? false, p.NeedsOpeningBalance, hasImported, lots, sales);
            }).ToList();

            var first = positions[0];
            return Results.Ok(new PositionDetailDto(first.Ticker, first.Cusip, first.Name, details));
        });

        // manual lot entry — the AddPosition / OpeningBalance flows. The response
        // carries the 30-day wash warning the WinForms dialogs surfaced.
        api.MapPost("/lots", (AddLotRequest req, CoreServices svc) =>
        {
            if (req.Shares <= 0m || req.TotalCost < 0m || string.IsNullOrWhiteSpace(req.Ticker))
                return Results.BadRequest(new { error = "shares must be positive and ticker non-empty" });
            if (svc.Accounts.GetById(req.AccountId) is null)
                return Results.BadRequest(new { error = $"account {req.AccountId} does not exist" });
            var washWarning = svc.Tax.BuyWashWarning(req.Ticker.Trim().ToUpperInvariant(),
                DateOnly.FromDateTime(DateTime.Now));
            svc.ManualEntry.AddLot(req.AccountId, req.Ticker, req.Shares, req.TotalCost,
                req.TradeDate, req.OpeningBalance);
            return Results.Ok(new { washWarning });
        });

        api.MapPut("/lots/{txId:long}", (long txId, UpdateLotRequest req, CoreServices svc) =>
        {
            if (req.Shares <= 0m || req.TotalCost < 0m)
                return Results.BadRequest(new { error = "shares must be positive" });
            try
            {
                svc.ManualEntry.UpdateLot(txId, req.Shares, req.TotalCost, req.TradeDate);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.NoContent();
        });

        api.MapDelete("/lots/{txId:long}", (long txId, CoreServices svc) =>
        {
            try
            {
                svc.ManualEntry.DeleteLot(txId);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.NoContent();
        });

        api.MapDelete("/positions/{positionId:long}", (long positionId, CoreServices svc) =>
        {
            svc.ManualEntry.DeletePosition(positionId);
            return Results.NoContent();
        });

        // cumulative-row delete: every account's position for the ticker
        api.MapDelete("/positions/ticker/{ticker}", (string ticker, CoreServices svc) =>
        {
            foreach (var p in svc.Positions.GetAllByTicker(ticker))
                svc.ManualEntry.DeletePosition(p.Id);
            return Results.NoContent();
        });
    }

    private static LotDto ToLotDto(Lot l, DateOnly today)
    {
        // a lot goes long-term the day AFTER the one-year anniversary
        var longTermOn = l.TradeDate.AddYears(1).AddDays(1);
        var days = longTermOn.DayNumber - today.DayNumber;
        return new LotDto(l.Id, l.Shares, l.TotalCost, l.RemainingShares, l.RemainingCost,
            l.TradeDate, l.Source, l.SourceTxId, longTermOn, days > 0 ? days : null);
    }
}
