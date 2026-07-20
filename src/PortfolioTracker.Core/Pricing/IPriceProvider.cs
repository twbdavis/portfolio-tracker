using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

/// Swap point for market data. Implementations must return null for tickers the
/// provider cannot resolve and throw PriceFetchException for transport/auth failures.
public interface IPriceProvider
{
    Task<PriceQuote?> GetQuoteAsync(string ticker, CancellationToken ct);
}

public class PriceFetchException(string message, Exception? inner = null) : Exception(message, inner);
