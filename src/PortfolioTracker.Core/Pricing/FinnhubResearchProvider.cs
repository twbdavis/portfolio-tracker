using System.Globalization;
using System.Net;
using System.Text.Json;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

public class FinnhubResearchProvider(HttpClient http, Func<string?> apiKey) : IResearchDataProvider
{
    /// GETs a Finnhub endpoint. Returns null when the free tier doesn't include it
    /// (402/403) or the symbol is unknown (404); throws PriceFetchException on
    /// transport failures so callers can distinguish "unavailable" from "broken".
    private async Task<JsonDocument?> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new PriceFetchException("Finnhub API key not set. Open Settings to add one.");
        var sep = pathAndQuery.Contains('?') ? '&' : '?';
        var url = $"https://finnhub.io/api/v1/{pathAndQuery}{sep}token={Uri.EscapeDataString(key)}";
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return null;
            if (resp.StatusCode == (HttpStatusCode)429)
                throw new PriceFetchException("Finnhub rate limit hit; try again in a minute.");
            if (!resp.IsSuccessStatusCode)
                throw new PriceFetchException($"Finnhub returned {(int)resp.StatusCode}.");
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw new PriceFetchException($"Network error: {ex.Message}", ex); }
        catch (JsonException ex) { throw new PriceFetchException($"Unreadable Finnhub response: {ex.Message}", ex); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new PriceFetchException("Finnhub request timed out."); }
    }

    private static decimal? Dec(JsonElement obj, params string[] candidateKeys)
    {
        foreach (var key in candidateKeys)
            if (obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number
                && el.TryGetDecimal(out var d))
                return d;
        return null;
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int Int(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;

    public async Task<List<SymbolSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        using var doc = await GetAsync($"search?q={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
        var results = new List<SymbolSearchResult>();
        if (doc is null || !doc.RootElement.TryGetProperty("result", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return results;
        foreach (var e in arr.EnumerateArray())
        {
            var symbol = Str(e, "displaySymbol") ?? Str(e, "symbol");
            if (string.IsNullOrEmpty(symbol)) continue;
            results.Add(new SymbolSearchResult(symbol, Str(e, "description") ?? "", Str(e, "type") ?? ""));
        }
        return results;
    }

    public async Task<QuoteDetail?> GetQuoteDetailAsync(string ticker, CancellationToken ct)
    {
        using var doc = await GetAsync($"quote?symbol={Uri.EscapeDataString(ticker)}", ct).ConfigureAwait(false);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var root = doc.RootElement;
        var current = Dec(root, "c") ?? 0m;
        var prevClose = Dec(root, "pc") ?? 0m;
        if (current == 0m && prevClose == 0m) return null;   // unknown symbol
        return new QuoteDetail(ticker, current, prevClose, DateTimeOffset.Now);
    }

    public async Task<CompanyProfile?> GetProfileAsync(string ticker, CancellationToken ct)
    {
        using var doc = await GetAsync($"stock/profile2?symbol={Uri.EscapeDataString(ticker)}", ct).ConfigureAwait(false);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var root = doc.RootElement;
        var name = Str(root, "name");
        if (string.IsNullOrEmpty(name)) return null;   // finnhub returns {} for unknown symbols
        DateOnly? ipo = DateOnly.TryParseExact(Str(root, "ipo"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
        return new CompanyProfile
        {
            Ticker = Str(root, "ticker") ?? ticker,
            Name = name,
            Industry = Str(root, "finnhubIndustry"),
            MarketCapMillions = Dec(root, "marketCapitalization"),
            IpoDate = ipo,
            WebUrl = Str(root, "weburl"),
        };
    }

    public async Task<FundamentalMetrics?> GetMetricsAsync(string ticker, CancellationToken ct)
    {
        using var doc = await GetAsync($"stock/metric?symbol={Uri.EscapeDataString(ticker)}&metric=all", ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("metric", out var m)
            || m.ValueKind != JsonValueKind.Object)
            return null;
        return new FundamentalMetrics
        {
            MarketCap = Dec(m, "marketCapitalization"),
            PeTtm = Dec(m, "peTTM", "peBasicExclExtraTTM", "peNormalizedAnnual"),
            Ps = Dec(m, "psTTM", "psAnnual"),
            Pb = Dec(m, "pb", "pbQuarterly", "pbAnnual"),
            EpsTtm = Dec(m, "epsTTM", "epsBasicExclExtraItemsTTM", "epsInclExtraItemsTTM"),
            RevenueGrowthYoY = Dec(m, "revenueGrowthTTMYoy", "revenueGrowthQuarterlyYoy"),
            RevenueGrowth3Y = Dec(m, "revenueGrowth3Y"),
            EpsGrowthYoY = Dec(m, "epsGrowthTTMYoy", "epsGrowthQuarterlyYoy"),
            GrossMargin = Dec(m, "grossMarginTTM", "grossMarginAnnual"),
            NetMargin = Dec(m, "netProfitMarginTTM", "netProfitMarginAnnual"),
            Roe = Dec(m, "roeTTM", "roeRfy"),
            Beta = Dec(m, "beta"),
            High52 = Dec(m, "52WeekHigh"),
            Low52 = Dec(m, "52WeekLow"),
            DividendYield = Dec(m, "currentDividendYieldTTM", "dividendYieldIndicatedAnnual"),
            DebtToEquity = Dec(m, "totalDebt/totalEquityQuarterly", "totalDebt/totalEquityAnnual"),
            CurrentRatio = Dec(m, "currentRatioQuarterly", "currentRatioAnnual"),
        };
    }

    public async Task<List<RecommendationTrend>> GetRecommendationsAsync(string ticker, CancellationToken ct)
    {
        using var doc = await GetAsync($"stock/recommendation?symbol={Uri.EscapeDataString(ticker)}", ct).ConfigureAwait(false);
        var trends = new List<RecommendationTrend>();
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return trends;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            trends.Add(new RecommendationTrend
            {
                Period = Str(e, "period") ?? "",
                StrongBuy = Int(e, "strongBuy"), Buy = Int(e, "buy"), Hold = Int(e, "hold"),
                Sell = Int(e, "sell"), StrongSell = Int(e, "strongSell"),
            });
        }
        // finnhub returns newest first; keep the latest 6 months
        return trends.OrderByDescending(t => t.Period).Take(6).ToList();
    }

    public async Task<List<EarningsQuarter>> GetEarningsAsync(string ticker, CancellationToken ct)
    {
        using var doc = await GetAsync($"stock/earnings?symbol={Uri.EscapeDataString(ticker)}", ct).ConfigureAwait(false);
        var quarters = new List<EarningsQuarter>();
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return quarters;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            quarters.Add(new EarningsQuarter
            {
                Period = Str(e, "period") ?? "",
                Estimate = Dec(e, "estimate"),
                Actual = Dec(e, "actual"),
                SurprisePercent = Dec(e, "surprisePercent"),
            });
        }
        return quarters.OrderByDescending(q => q.Period).Take(4).ToList();
    }

    public async Task<DateOnly?> GetNextEarningsDateAsync(string ticker, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(DateTime.Today);
        var to = from.AddMonths(4);
        using var doc = await GetAsync(
            $"calendar/earnings?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&symbol={Uri.EscapeDataString(ticker)}", ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("earningsCalendar", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return null;
        DateOnly? next = null;
        foreach (var e in arr.EnumerateArray())
        {
            if (!DateOnly.TryParseExact(Str(e, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d) || d < from)
                continue;
            if (next is null || d < next) next = d;
        }
        return next;
    }

    public async Task<List<NewsItem>> GetCompanyNewsAsync(string ticker, DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var doc = await GetAsync(
            $"company-news?symbol={Uri.EscapeDataString(ticker)}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct).ConfigureAwait(false);
        var items = new List<NewsItem>();
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return items;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var headline = Str(e, "headline");
            var url = Str(e, "url");
            if (string.IsNullOrEmpty(headline) || string.IsNullOrEmpty(url)) continue;
            var unix = e.TryGetProperty("datetime", out var dt) && dt.ValueKind == JsonValueKind.Number
                ? dt.GetInt64() : 0;
            items.Add(new NewsItem
            {
                PublishedAt = DateTimeOffset.FromUnixTimeSeconds(unix),
                Source = Str(e, "source") ?? "",
                Headline = headline,
                Url = url,
                Summary = Str(e, "summary"),
            });
        }
        return items.OrderByDescending(n => n.PublishedAt).ToList();
    }

    public async Task<List<string>> GetPeersAsync(string ticker, CancellationToken ct)
    {
        using var doc = await GetAsync($"stock/peers?symbol={Uri.EscapeDataString(ticker)}", ct).ConfigureAwait(false);
        var peers = new List<string>();
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return peers;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) continue;
            var symbol = e.GetString()?.Trim().ToUpperInvariant();
            // finnhub includes the query symbol itself in its peer list
            if (string.IsNullOrEmpty(symbol)
                || string.Equals(symbol, ticker.Trim().ToUpperInvariant(), StringComparison.OrdinalIgnoreCase))
                continue;
            peers.Add(symbol);
        }
        return peers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
