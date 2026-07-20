using System.Net;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Core.Tests;

public class YahooHistoryProviderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // 2026-07-16 13:30 UTC and 2026-07-17 13:30 UTC session opens
    private const string ValidChart = """
        {"chart":{"result":[{"meta":{"symbol":"NVDA"},
          "timestamp":[1784208600,1784295000],
          "indicators":{
            "quote":[{"close":[101.0,102.0]}],
            "adjclose":[{"adjclose":[100.5,101.5]}]}}],
          "error":null}}
        """;

    [Fact]
    public void Parses_bars_preferring_adjusted_close()
    {
        var bars = YahooHistoryProvider.ParseChartJson(ValidChart);
        Assert.Equal(2, bars.Count);
        Assert.Equal(new DateOnly(2026, 7, 16), bars[0].Date);
        Assert.Equal(100.5m, bars[0].Close);   // adjclose, not raw close
        Assert.Equal(101.5m, bars[1].Close);
    }

    [Fact]
    public void Falls_back_to_raw_close_without_adjclose()
    {
        var json = """
            {"chart":{"result":[{"timestamp":[1784208600],
              "indicators":{"quote":[{"close":[101.0]}]}}],"error":null}}
            """;
        var bars = YahooHistoryProvider.ParseChartJson(json);
        Assert.Equal(101.0m, bars.Single().Close);
    }

    [Fact]
    public void Null_closes_from_halts_are_skipped()
    {
        var json = """
            {"chart":{"result":[{"timestamp":[1784208600,1784295000,1784381400],
              "indicators":{"quote":[{"close":[101.0,null,103.0]}]}}],"error":null}}
            """;
        var bars = YahooHistoryProvider.ParseChartJson(json);
        Assert.Equal(2, bars.Count);
        Assert.Equal(103.0m, bars[1].Close);
    }

    [Fact]
    public void Error_object_means_unknown_symbol_empty_list()
    {
        var json = """
            {"chart":{"result":null,"error":{"code":"Not Found","description":"No data found"}}}
            """;
        Assert.Empty(YahooHistoryProvider.ParseChartJson(json));
    }

    [Fact]
    public void Valid_symbol_with_no_bars_is_empty()
    {
        Assert.Empty(YahooHistoryProvider.ParseChartJson(
            """{"chart":{"result":[{"meta":{}}],"error":null}}"""));
    }

    [Fact]
    public void Bot_challenge_html_throws_instead_of_stamping_empty()
    {
        // the Stooq failure mode this provider must never repeat: an HTML
        // challenge page must be a fetch ERROR, not "ticker has no history"
        Assert.Throws<PriceFetchException>(() =>
            YahooHistoryProvider.ParseChartJson("<!DOCTYPE html><html>verify your browser</html>"));
    }

    [Fact]
    public void Unexpected_json_shape_throws()
    {
        Assert.Throws<PriceFetchException>(() =>
            YahooHistoryProvider.ParseChartJson("""{"unexpected":true}"""));
        Assert.Throws<PriceFetchException>(() => YahooHistoryProvider.ParseChartJson(
            """{"chart":{"result":[{"timestamp":[1784208600],"indicators":{}}],"error":null}}"""));
    }

    [Theory]
    [InlineData("NVDA", "NVDA")]
    [InlineData("BRK.B", "BRK-B")]
    [InlineData(" voo ", "VOO")]
    public void Ticker_maps_to_yahoo_symbol(string ticker, string expected) =>
        Assert.Equal(expected, YahooHistoryProvider.ToYahooSymbol(ticker));

    [Fact]
    public async Task Request_carries_user_agent_and_period_bounds()
    {
        var handler = new FakeHandler(_ => Json(ValidChart));
        var provider = new YahooHistoryProvider(new HttpClient(handler));
        var bars = await provider.GetDailyHistoryAsync("BRK.B", new DateOnly(2025, 1, 2), CancellationToken.None);
        Assert.Equal(2, bars.Count);
        var req = handler.Requests.Single();
        Assert.Contains("/v8/finance/chart/BRK-B", req.RequestUri!.ToString());
        Assert.Contains("interval=1d", req.RequestUri.ToString());
        Assert.Contains("period1=", req.RequestUri.ToString());
        Assert.True(req.Headers.Contains("User-Agent"));
    }

    [Fact]
    public async Task Http_404_is_unknown_symbol_429_throws()
    {
        var notFound = new YahooHistoryProvider(new HttpClient(
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Empty(await notFound.GetDailyHistoryAsync("ZZZZ", null, CancellationToken.None));

        var limited = new YahooHistoryProvider(new HttpClient(
            new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)429))));
        await Assert.ThrowsAsync<PriceFetchException>(
            () => limited.GetDailyHistoryAsync("NVDA", null, CancellationToken.None));
    }
}
