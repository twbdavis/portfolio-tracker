using System.Net.Http.Json;
using System.Text.Json;

namespace PortfolioTracker.Api.Tests;

public class SettingsTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public SettingsTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Settings_round_trip()
    {
        var initial = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("dark", initial.GetProperty("theme").GetString());

        var put = await _fx.Client.PutAsJsonAsync("/api/settings", new
        {
            finnhubApiKey = "test-key",
            refreshIntervalSeconds = 120,
            showClosedPositions = true,
            theme = "light",
            filingStatus = "MarriedFilingJointly",
            otherIncome = 85000m,
            startWithWindows = false,
        });
        put.EnsureSuccessStatusCode();

        var updated = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("test-key", updated.GetProperty("finnhubApiKey").GetString());
        Assert.Equal(120, updated.GetProperty("refreshIntervalSeconds").GetInt32());
        Assert.True(updated.GetProperty("showClosedPositions").GetBoolean());
        Assert.Equal("light", updated.GetProperty("theme").GetString());
        Assert.Equal("MarriedFilingJointly", updated.GetProperty("filingStatus").GetString());
        Assert.Equal(85000m, updated.GetProperty("otherIncome").GetDecimal());
    }

    [Fact]
    public async Task Settings_validation_rejects_bad_input()
    {
        var badInterval = await _fx.Client.PutAsJsonAsync("/api/settings", new
        {
            finnhubApiKey = (string?)null, refreshIntervalSeconds = 5, showClosedPositions = false,
            theme = "dark", filingStatus = "Single", otherIncome = 0m, startWithWindows = false,
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badInterval.StatusCode);

        var badIncome = await _fx.Client.PutAsJsonAsync("/api/settings", new
        {
            finnhubApiKey = (string?)null, refreshIntervalSeconds = 60, showClosedPositions = false,
            theme = "dark", filingStatus = "Single", otherIncome = -1m, startWithWindows = false,
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badIncome.StatusCode);
    }

    [Fact]
    public async Task Test_connection_without_key_reports_not_ok()
    {
        // fixture db starts keyless unless another test set one — reset first
        await _fx.Client.PutAsJsonAsync("/api/settings", new
        {
            finnhubApiKey = (string?)null, refreshIntervalSeconds = 60, showClosedPositions = false,
            theme = "dark", filingStatus = "Single", otherIncome = 0m, startWithWindows = false,
        });
        var resp = await _fx.Client.PostAsync("/api/settings/test-connection", null);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Account_summary_lists_counts_and_delete_cascades()
    {
        var core = _fx.Host.Core;
        var account = new Core.Models.Account
        {
            AccountNumber = "...2222", DisplayName = "Doomed", CreatedAt = DateTimeOffset.UtcNow,
        };
        core.Accounts.Insert(account);
        core.ManualEntry.AddLot(account.Id, "ZZZT", 1m, 10m, new DateOnly(2026, 1, 5), openingBalance: false);

        var summary = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/settings/accounts");
        var row = summary.EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == account.Id);
        Assert.Equal(1, row.GetProperty("positions").GetInt32());
        Assert.Equal(1, row.GetProperty("transactions").GetInt32());

        var del = await _fx.Client.DeleteAsync($"/api/accounts/{account.Id}");
        del.EnsureSuccessStatusCode();
        summary = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/settings/accounts");
        Assert.DoesNotContain(summary.EnumerateArray(),
            a => a.GetProperty("id").GetInt64() == account.Id);
    }
}
