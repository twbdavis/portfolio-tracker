using System.Net.Http.Json;
using System.Text.Json;

namespace PortfolioTracker.Api.Tests;

public class TargetsWatchlistTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public TargetsWatchlistTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Target_upsert_get_rearm_cycle()
    {
        var put = await _fx.Client.PutAsJsonAsync("/api/targets/MSFT", new
        {
            targetPrice = 500m, targetDirection = "AtOrAbove", alertEnabled = true, notes = "watch it",
        });
        put.EnsureSuccessStatusCode();

        var target = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/targets/MSFT");
        Assert.Equal(500m, target.GetProperty("targetPrice").GetDecimal());
        Assert.Equal("AtOrAbove", target.GetProperty("targetDirection").GetString());
        Assert.True(target.GetProperty("alertEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("alertTriggeredAt").ValueKind);

        var all = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/targets");
        Assert.Contains(all.EnumerateArray(), t => t.GetProperty("ticker").GetString() == "MSFT");

        // fire the alert, then verify a same-price save does NOT re-arm
        _fx.Host.Core.TickerTargets.StampAlert("MSFT", DateTimeOffset.Now);
        put = await _fx.Client.PutAsJsonAsync("/api/targets/MSFT", new
        {
            targetPrice = 500m, targetDirection = "AtOrAbove", alertEnabled = true, notes = "watch it",
        });
        put.EnsureSuccessStatusCode();
        target = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/targets/MSFT");
        Assert.NotEqual(JsonValueKind.Null, target.GetProperty("alertTriggeredAt").ValueKind);

        // a price change re-arms (clears alertTriggeredAt)
        put = await _fx.Client.PutAsJsonAsync("/api/targets/MSFT", new
        {
            targetPrice = 550m, targetDirection = "AtOrAbove", alertEnabled = true, notes = "watch it",
        });
        put.EnsureSuccessStatusCode();
        target = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/targets/MSFT");
        Assert.Equal(JsonValueKind.Null, target.GetProperty("alertTriggeredAt").ValueKind);

        // the explicit re-arm endpoint
        _fx.Host.Core.TickerTargets.StampAlert("MSFT", DateTimeOffset.Now);
        var rearm = await _fx.Client.PostAsync("/api/targets/MSFT/rearm", null);
        rearm.EnsureSuccessStatusCode();
        target = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/targets/MSFT");
        Assert.Equal(JsonValueKind.Null, target.GetProperty("alertTriggeredAt").ValueKind);

        var del = await _fx.Client.DeleteAsync("/api/targets/MSFT");
        del.EnsureSuccessStatusCode();
        target = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/targets/MSFT");
        Assert.Equal(JsonValueKind.Null, target.GetProperty("targetPrice").ValueKind);
        Assert.Equal(0, target.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Watchlist_crud_and_bought_flow()
    {
        var post = await _fx.Client.PostAsJsonAsync("/api/watchlist", new
        {
            ticker = "amd", thesis = "GPU cycle", conviction = 4, targetBuyPrice = 100m, alertEnabled = true,
        });
        post.EnsureSuccessStatusCode();
        var created = await post.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt64();
        Assert.Equal("AMD", created.GetProperty("ticker").GetString());

        // duplicate ticker is rejected
        var dup = await _fx.Client.PostAsJsonAsync("/api/watchlist", new
        {
            ticker = "AMD", thesis = "", conviction = 3, targetBuyPrice = (decimal?)null, alertEnabled = false,
        });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, dup.StatusCode);

        // editing the buy target re-arms a triggered alert
        _fx.Host.Core.Watchlist.StampAlert(id, DateTimeOffset.Now);
        var put = await _fx.Client.PutAsJsonAsync($"/api/watchlist/{id}", new
        {
            thesis = "GPU cycle", conviction = 4, targetBuyPrice = 90m, alertEnabled = true,
        });
        put.EnsureSuccessStatusCode();
        var item = (await _fx.Client.GetFromJsonAsync<JsonElement>("/api/watchlist"))
            .EnumerateArray().Single(w => w.GetProperty("id").GetInt64() == id);
        Assert.Equal(90m, item.GetProperty("targetBuyPrice").GetDecimal());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("alertTriggeredAt").ValueKind);

        // the explicit re-arm endpoint
        _fx.Host.Core.Watchlist.StampAlert(id, DateTimeOffset.Now);
        var rearm = await _fx.Client.PostAsync($"/api/watchlist/{id}/rearm", null);
        rearm.EnsureSuccessStatusCode();
        item = (await _fx.Client.GetFromJsonAsync<JsonElement>("/api/watchlist"))
            .EnumerateArray().Single(w => w.GetProperty("id").GetInt64() == id);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("alertTriggeredAt").ValueKind);

        // "I bought it": creates a lot, carries the thesis into position notes,
        // and keeps the watchlist row (the frontend asks keep-or-remove after)
        var core = _fx.Host.Core;
        var account = new Core.Models.Account
        {
            AccountNumber = "...2222", DisplayName = "Watch Test", CreatedAt = DateTimeOffset.UtcNow,
        };
        core.Accounts.Insert(account);
        var bought = await _fx.Client.PostAsJsonAsync($"/api/watchlist/{id}/bought", new
        {
            accountId = account.Id, shares = 5m, totalCost = 450m, tradeDate = "2026-07-01",
        });
        bought.EnsureSuccessStatusCode();
        var boughtBody = await bought.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, boughtBody.GetProperty("washWarning").ValueKind);

        var detail = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/positions/AMD/detail");
        var lot = detail.GetProperty("accounts").EnumerateArray().Single()
            .GetProperty("lots").EnumerateArray().Single();
        Assert.Equal(5m, lot.GetProperty("remainingShares").GetDecimal());
        Assert.Equal(450m, lot.GetProperty("remainingCost").GetDecimal());

        var pos = core.Positions.GetByAccountAndTicker(account.Id, "AMD");
        Assert.Contains("Watchlist thesis (4/5): GPU cycle", pos!.Notes);

        var stillThere = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/watchlist");
        Assert.Contains(stillThere.EnumerateArray(), w => w.GetProperty("id").GetInt64() == id);

        var del = await _fx.Client.DeleteAsync($"/api/watchlist/{id}");
        del.EnsureSuccessStatusCode();
        var after = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/watchlist");
        Assert.DoesNotContain(after.EnumerateArray(), w => w.GetProperty("id").GetInt64() == id);
    }

    [Fact]
    public async Task Research_search_without_key_is_json_error_not_500()
    {
        var resp = await _fx.Client.GetAsync("/api/research/search?q=apple");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("results").EnumerateArray());
        Assert.Contains("Finnhub API key", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("fundamentals")]
    [InlineData("recommendations")]
    [InlineData("earnings")]
    [InlineData("news")]
    public async Task Research_panels_without_key_are_json_errors_not_500(string panel)
    {
        var resp = await _fx.Client.GetAsync($"/api/research/AAPL/{panel}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Finnhub API key", body.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("value").ValueKind);
    }
}
