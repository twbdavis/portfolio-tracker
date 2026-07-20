using System.Net;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Core.Tests;

public class StooqHistoryProviderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Csv(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private const string ValidCsv = """
        Date,Open,High,Low,Close,Volume
        2026-07-15,100.5,102.0,99.8,101.25,1000000
        2026-07-16,101.3,103.1,101.0,102.75,1200000
        2026-07-17,102.8,104.0,102.1,103.5,900000
        """;

    [Fact]
    public void Parses_valid_csv_oldest_first()
    {
        var bars = StooqHistoryProvider.ParseCsv(ValidCsv);
        Assert.Equal(3, bars.Count);
        Assert.Equal(new DateOnly(2026, 7, 15), bars[0].Date);
        Assert.Equal(101.25m, bars[0].Close);
        Assert.Equal(103.5m, bars[2].Close);
    }

    [Fact]
    public void Sorts_rows_that_arrive_newest_first()
    {
        var bars = StooqHistoryProvider.ParseCsv("""
            Date,Open,High,Low,Close,Volume
            2026-07-17,102.8,104.0,102.1,103.5,900000
            2026-07-15,100.5,102.0,99.8,101.25,1000000
            """);
        Assert.Equal(new DateOnly(2026, 7, 15), bars[0].Date);
        Assert.Equal(new DateOnly(2026, 7, 17), bars[1].Date);
    }

    [Fact]
    public void No_data_response_yields_empty_list()
    {
        Assert.Empty(StooqHistoryProvider.ParseCsv("No data"));
        Assert.Empty(StooqHistoryProvider.ParseCsv(""));
        Assert.Empty(StooqHistoryProvider.ParseCsv("   "));
        Assert.Empty(StooqHistoryProvider.ParseCsv("<html><body>error</body></html>"));
    }

    [Fact]
    public void Header_only_yields_empty_list()
    {
        Assert.Empty(StooqHistoryProvider.ParseCsv("Date,Open,High,Low,Close,Volume\n"));
    }

    [Fact]
    public void Malformed_rows_are_skipped_not_fatal()
    {
        var bars = StooqHistoryProvider.ParseCsv("""
            Date,Open,High,Low,Close,Volume
            2026-07-15,100.5,102.0,99.8,101.25,1000000
            not-a-date,1,2,3,4,5
            2026-07-16,101.3,103.1
            2026-07-17,102.8,104.0,102.1,,900000
            2026-07-18,102.8,104.0,102.1,0,900000
            2026-07-20,104.0,105.0,103.5,104.5,800000
            """);
        Assert.Equal(2, bars.Count);
        Assert.Equal(new DateOnly(2026, 7, 15), bars[0].Date);
        Assert.Equal(new DateOnly(2026, 7, 20), bars[1].Date);
    }

    [Fact]
    public void Windows_line_endings_parse()
    {
        var bars = StooqHistoryProvider.ParseCsv(
            "Date,Open,High,Low,Close,Volume\r\n2026-07-15,100.5,102.0,99.8,101.25,1000000\r\n");
        Assert.Single(bars);
        Assert.Equal(101.25m, bars[0].Close);
    }

    [Theory]
    [InlineData("NVDA", "nvda.us")]
    [InlineData("BRK.B", "brk-b.us")]
    [InlineData(" voo ", "voo.us")]
    public void Ticker_maps_to_stooq_symbol(string ticker, string expected) =>
        Assert.Equal(expected, StooqHistoryProvider.ToStooqSymbol(ticker));

    [Fact]
    public async Task Builds_url_with_symbol_and_date_bounds()
    {
        var handler = new FakeHandler(_ => Csv(ValidCsv));
        var provider = new StooqHistoryProvider(new HttpClient(handler));
        var bars = await provider.GetDailyHistoryAsync("BRK.B", new DateOnly(2025, 1, 2), CancellationToken.None);
        Assert.Equal(3, bars.Count);
        var url = handler.Requests.Single().ToString();
        Assert.Contains("s=brk-b.us", url);
        Assert.Contains("i=d", url);
        Assert.Contains("d1=20250102", url);
    }

    [Fact]
    public async Task Missing_ticker_degrades_to_empty()
    {
        var handler = new FakeHandler(_ => Csv("No data"));
        var provider = new StooqHistoryProvider(new HttpClient(handler));
        Assert.Empty(await provider.GetDailyHistoryAsync("ZZZZ", null, CancellationToken.None));
    }

    [Fact]
    public async Task Server_error_throws_PriceFetchException()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = new StooqHistoryProvider(new HttpClient(handler));
        await Assert.ThrowsAsync<PriceFetchException>(
            () => provider.GetDailyHistoryAsync("NVDA", null, CancellationToken.None));
    }
}
