namespace PortfolioTracker.Core.Models;

public record PriceQuote(string Ticker, decimal Price, DateTimeOffset FetchedAt, decimal? PrevClose = null);
