using System.Text.Json.Serialization;

namespace PortfolioTracker.Core.Models;

public record SymbolSearchResult(string Symbol, string Description, string Type);

public record QuoteDetail(string Ticker, decimal Current, decimal PrevClose, DateTimeOffset FetchedAt)
{
    public decimal DayChange => Current - PrevClose;
    public decimal? DayChangePct => PrevClose == 0m ? null : DayChange / PrevClose * 100m;
}

public class CompanyProfile
{
    public string Ticker { get; set; } = "";
    public string? Name { get; set; }
    public string? Industry { get; set; }
    public decimal? MarketCapMillions { get; set; }   // finnhub profile2 reports millions
    public DateOnly? IpoDate { get; set; }
    public string? WebUrl { get; set; }
}

/// Nullable throughout: ETFs and small caps legitimately lack most of these.
public class FundamentalMetrics
{
    public decimal? MarketCap { get; set; }   // millions, like profile2
    public decimal? PeTtm { get; set; }
    public decimal? Ps { get; set; }
    public decimal? Pb { get; set; }
    public decimal? EpsTtm { get; set; }
    public decimal? RevenueGrowthYoY { get; set; }
    public decimal? RevenueGrowth3Y { get; set; }
    public decimal? EpsGrowthYoY { get; set; }
    public decimal? GrossMargin { get; set; }
    public decimal? NetMargin { get; set; }
    public decimal? Roe { get; set; }
    public decimal? Beta { get; set; }
    public decimal? High52 { get; set; }
    public decimal? Low52 { get; set; }
    public decimal? DividendYield { get; set; }
    public decimal? DebtToEquity { get; set; }
    public decimal? CurrentRatio { get; set; }
}

public class RecommendationTrend
{
    public string Period { get; set; } = "";   // "2026-06-01"
    public int StrongBuy { get; set; }
    public int Buy { get; set; }
    public int Hold { get; set; }
    public int Sell { get; set; }
    public int StrongSell { get; set; }

    [JsonIgnore] public int Total => StrongBuy + Buy + Hold + Sell + StrongSell;

    /// (strong buy + buy) / total analysts; null when no analysts cover the ticker.
    [JsonIgnore] public decimal? BuyRatio => Total == 0 ? null : (decimal)(StrongBuy + Buy) / Total;
}

public class EarningsQuarter
{
    public string Period { get; set; } = "";   // "2026-03-31"
    public decimal? Estimate { get; set; }
    public decimal? Actual { get; set; }
    public decimal? SurprisePercent { get; set; }
}

public class NewsItem
{
    public DateTimeOffset PublishedAt { get; set; }
    public string Source { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Summary { get; set; }
}
