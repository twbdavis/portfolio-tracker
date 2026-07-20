using System.Net;
using System.Net.Http.Json;

namespace PortfolioTracker.Api.Tests;

public class HostSmokeTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public HostSmokeTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Binds_loopback_random_port()
    {
        Assert.Equal("127.0.0.1", _fx.Host.BaseUri.Host);
        Assert.NotEqual(80, _fx.Host.BaseUri.Port);
    }

    [Fact]
    public async Task Api_requires_the_session_token()
    {
        var denied = await _fx.AnonymousClient.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var ok = await _fx.Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Static_shell_is_served_without_token()
    {
        var resp = await _fx.AnonymousClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("PortfolioTracker", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Accounts_endpoint_round_trips()
    {
        var accounts = await _fx.Client.GetFromJsonAsync<List<AccountJson>>("/api/accounts");
        Assert.NotNull(accounts);   // fresh db: empty but valid
    }

    private record AccountJson(long Id, string AccountNumber, string DisplayName, string TaxTreatment);
}
