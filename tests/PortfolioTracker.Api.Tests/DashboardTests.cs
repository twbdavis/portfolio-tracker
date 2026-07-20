using System.Net.Http.Json;
using System.Text.Json;

namespace PortfolioTracker.Api.Tests;

public class DashboardTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public DashboardTests(ApiFixture fx) => _fx = fx;

    private record LotResponse(string? WashWarning);

    [Fact]
    public async Task Lot_crud_flows_into_dashboard_and_detail()
    {
        // seed an account directly, then drive everything else over HTTP
        var core = _fx.Host.Core;
        var account = new Core.Models.Account
        {
            AccountNumber = "...1111", DisplayName = "HTTP Test", CreatedAt = DateTimeOffset.UtcNow,
        };
        core.Accounts.Insert(account);

        var add = await _fx.Client.PostAsJsonAsync("/api/lots", new
        {
            accountId = account.Id, ticker = "NVDA", shares = 2m, totalCost = 300m,
            tradeDate = "2026-01-05", openingBalance = false,
        });
        add.EnsureSuccessStatusCode();

        var cumulative = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/cumulative");
        var row = cumulative.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("ticker").GetString() == "NVDA");
        Assert.Equal(2m, row.GetProperty("shares").GetDecimal());
        Assert.Equal(300m, row.GetProperty("costBasis").GetDecimal());
        Assert.Equal(300m, cumulative.GetProperty("summary").GetProperty("totalCostBasis").GetDecimal());

        var perAccount = await _fx.Client.GetFromJsonAsync<JsonElement>($"/api/dashboard/account/{account.Id}");
        Assert.Single(perAccount.GetProperty("rows").EnumerateArray());

        var detail = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/positions/NVDA/detail");
        var acct = detail.GetProperty("accounts").EnumerateArray().Single();
        var lot = acct.GetProperty("lots").EnumerateArray().Single();
        Assert.Equal(2m, lot.GetProperty("remainingShares").GetDecimal());
        var txId = lot.GetProperty("sourceTxId").GetInt64();

        var edit = await _fx.Client.PutAsJsonAsync($"/api/lots/{txId}", new
        {
            shares = 3m, totalCost = 450m, tradeDate = "2026-01-06",
        });
        edit.EnsureSuccessStatusCode();
        detail = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/positions/NVDA/detail");
        lot = detail.GetProperty("accounts").EnumerateArray().Single()
            .GetProperty("lots").EnumerateArray().Single();
        Assert.Equal(3m, lot.GetProperty("shares").GetDecimal());

        var del = await _fx.Client.DeleteAsync($"/api/lots/{txId}");
        del.EnsureSuccessStatusCode();
        detail = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/positions/NVDA/detail");
        Assert.Empty(detail.GetProperty("accounts").EnumerateArray().Single()
            .GetProperty("lots").EnumerateArray());
    }

    [Fact]
    public async Task Ytd_tax_realized_matches_the_taxes_page_after_tax_number()
    {
        var core = _fx.Host.Core;
        var account = new Core.Models.Account
        {
            AccountNumber = "...2222", DisplayName = "AfterTax Test", CreatedAt = DateTimeOffset.UtcNow,
        };
        core.Accounts.Insert(account);

        // one realized gain this year: buy 10 @ 100, sell 10 @ 150 → +50 gross
        core.ManualEntry.AddLot(account.Id, "AFTX", 10m, 1000m, new DateOnly(DateTime.Now.Year, 1, 5), false);
        core.Transactions.Insert(new Core.Models.Transaction
        {
            AccountId = account.Id, Ticker = "AFTX", TradeDate = new DateOnly(DateTime.Now.Year, 2, 5),
            Type = Core.Models.TxType.Sell, Shares = 10m, Amount = 1500m,
            Source = Core.Models.LotSource.Manual,
        });
        core.Tax.RebuildAll();
        // income above the standard deduction so the gain is actually taxed
        core.Tax.Settings.SetOtherIncome(100_000m);

        var dashboard = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/cumulative");
        var ytd = dashboard.GetProperty("summary").GetProperty("ytdTaxRealized").GetDecimal();

        var tax = await _fx.Client.GetFromJsonAsync<JsonElement>($"/api/tax/{DateTime.Now.Year}");
        var expected = tax.GetProperty("yearShortTerm").GetDecimal()
            + tax.GetProperty("yearLongTerm").GetDecimal()
            - tax.GetProperty("estimate").GetProperty("attributable").GetDecimal();

        Assert.Equal(expected, ytd);
        // the estimate deducts something, so the tile is below gross realized
        var gross = dashboard.GetProperty("summary").GetProperty("realized").GetDecimal();
        Assert.True(ytd < gross, $"after-tax {ytd} should be below gross {gross}");
    }

    [Fact]
    public async Task Unknown_account_dashboard_is_404()
    {
        var resp = await _fx.Client.GetAsync("/api/dashboard/account/999999");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }
}
