namespace PortfolioTracker.Core.Pricing;

public record DailyBar(DateOnly Date, decimal Close);

/// Historical daily closes. Same contract style as IPriceProvider: an empty
/// list means the provider has no data for this ticker (coverage gaps are
/// expected — callers degrade momentum features for that ticker, everything
/// else keeps working); PriceFetchException means transport failure.
public interface IHistoryProvider
{
    /// Daily bars ordered oldest-first. `from` bounds the request when the
    /// provider supports it; callers must still tolerate extra rows.
    Task<List<DailyBar>> GetDailyHistoryAsync(string ticker, DateOnly? from, CancellationToken ct);
}
