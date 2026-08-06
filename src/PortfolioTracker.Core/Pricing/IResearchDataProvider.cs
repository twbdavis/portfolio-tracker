using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

/// Research data source. Same contract style as IPriceProvider: null/empty means
/// "the provider has nothing for this ticker or the endpoint isn't on our plan"
/// (callers degrade the panel); PriceFetchException means transport/auth failure.
public interface IResearchDataProvider
{
    Task<List<SymbolSearchResult>> SearchAsync(string query, CancellationToken ct);
    Task<QuoteDetail?> GetQuoteDetailAsync(string ticker, CancellationToken ct);
    Task<CompanyProfile?> GetProfileAsync(string ticker, CancellationToken ct);
    Task<FundamentalMetrics?> GetMetricsAsync(string ticker, CancellationToken ct);
    Task<List<RecommendationTrend>> GetRecommendationsAsync(string ticker, CancellationToken ct);
    Task<List<EarningsQuarter>> GetEarningsAsync(string ticker, CancellationToken ct);
    Task<DateOnly?> GetNextEarningsDateAsync(string ticker, CancellationToken ct);
    Task<List<NewsItem>> GetCompanyNewsAsync(string ticker, DateOnly from, DateOnly to, CancellationToken ct);
    /// Peer tickers for one symbol (the symbol itself excluded). Empty when
    /// the provider has no peer data for it.
    Task<List<string>> GetPeersAsync(string ticker, CancellationToken ct);
}
