using System.Net;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Core.Tests;

public class FinnhubPriceProviderTests
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

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Parses_current_price_and_builds_correct_url()
    {
        var handler = new FakeHandler(_ => Json("""{"c":195.12,"h":196.0,"l":190.1,"o":191.0,"pc":192.3,"t":1719936000}"""));
        var provider = new FinnhubPriceProvider(new HttpClient(handler), () => "KEY123");
        var q = await provider.GetQuoteAsync("NVDA", CancellationToken.None);
        Assert.Equal(195.12m, q!.Price);
        Assert.Equal("NVDA", q.Ticker);
        var url = handler.Requests.Single().ToString();
        Assert.Contains("symbol=NVDA", url);
        Assert.Contains("token=KEY123", url);
    }

    [Fact]
    public async Task Zero_current_and_prev_close_means_unresolvable()
    {
        var handler = new FakeHandler(_ => Json("""{"c":0,"h":0,"l":0,"o":0,"pc":0,"t":0}"""));
        var provider = new FinnhubPriceProvider(new HttpClient(handler), () => "KEY123");
        Assert.Null(await provider.GetQuoteAsync("NOPE", CancellationToken.None));
    }

    [Fact]
    public async Task Http_error_throws_PriceFetchException()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var provider = new FinnhubPriceProvider(new HttpClient(handler), () => "KEY123");
        await Assert.ThrowsAsync<PriceFetchException>(() => provider.GetQuoteAsync("NVDA", CancellationToken.None));
    }

    [Fact]
    public async Task Missing_api_key_throws_PriceFetchException()
    {
        var provider = new FinnhubPriceProvider(new HttpClient(new FakeHandler(_ => Json("{}"))), () => null);
        await Assert.ThrowsAsync<PriceFetchException>(() => provider.GetQuoteAsync("NVDA", CancellationToken.None));
    }
}
