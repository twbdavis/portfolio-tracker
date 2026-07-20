using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PortfolioTracker.Api.Tests;

public class PositionTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public PositionTests(ApiFixture fx) => _fx = fx;

    private async Task ImportSampleAsync()
    {
        using var content = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync("chase-transactions-sample.csv");
        content.Add(new ByteArrayContent(bytes), "file", "chase-transactions-sample.csv");
        var resp = await _fx.Client.PostAsync("/api/import/preview", content);
        resp.EnsureSuccessStatusCode();
        var preview = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var commit = await _fx.Client.PostAsJsonAsync("/api/import/commit", new
        {
            previewId = preview.GetProperty("previewId").GetString(),
            newAccounts = Array.Empty<object>(),
        });
        commit.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Updating_then_deleting_an_imported_lot_succeeds()
    {
        await ImportSampleAsync();
        var detail = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/positions/DRAM/detail");
        var lot = detail.GetProperty("accounts").EnumerateArray()
            .SelectMany(a => a.GetProperty("lots").EnumerateArray())
            .First(l => l.GetProperty("source").GetString() == "ChaseImport");
        var txId = lot.GetProperty("sourceTxId").GetInt64();

        var resp = await _fx.Client.PutAsJsonAsync($"/api/lots/{txId}", new
        {
            shares = 2m,
            totalCost = 100m,
            tradeDate = "2026-07-06",
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var after = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/positions/DRAM/detail");
        var updated = after.GetProperty("accounts").EnumerateArray()
            .SelectMany(a => a.GetProperty("lots").EnumerateArray())
            .First(l => l.GetProperty("sourceTxId").GetInt64() == txId);
        Assert.Equal(2m, updated.GetProperty("shares").GetDecimal());
        Assert.Equal(100m, updated.GetProperty("totalCost").GetDecimal());
        // the edited row keeps its dedupe key: source stays ChaseImport
        Assert.Equal("ChaseImport", updated.GetProperty("source").GetString());

        var del = await _fx.Client.DeleteAsync($"/api/lots/{txId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var afterDelete = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/positions/DRAM/detail");
        Assert.DoesNotContain(afterDelete.GetProperty("accounts").EnumerateArray()
            .SelectMany(a => a.GetProperty("lots").EnumerateArray()),
            l => l.GetProperty("sourceTxId").ValueKind != JsonValueKind.Null
                 && l.GetProperty("sourceTxId").GetInt64() == txId);
    }

    [Fact]
    public async Task Updating_a_manual_lot_succeeds_and_rebuilds()
    {
        await ImportSampleAsync();
        var accounts = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/settings/accounts");
        var accountId = accounts.EnumerateArray().First().GetProperty("id").GetInt64();

        var add = await _fx.Client.PostAsJsonAsync("/api/lots", new
        {
            accountId,
            ticker = "MANU",
            shares = 5m,
            totalCost = 50m,
            tradeDate = "2026-01-02",
            openingBalance = false,
        });
        add.EnsureSuccessStatusCode();

        var detail = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/positions/MANU/detail");
        var lot = detail.GetProperty("accounts").EnumerateArray()
            .SelectMany(a => a.GetProperty("lots").EnumerateArray())
            .First(l => l.GetProperty("source").GetString() == "Manual");
        var txId = lot.GetProperty("sourceTxId").GetInt64();

        var resp = await _fx.Client.PutAsJsonAsync($"/api/lots/{txId}", new
        {
            shares = 8m,
            totalCost = 96m,
            tradeDate = "2026-01-03",
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var after = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/positions/MANU/detail");
        var updated = after.GetProperty("accounts").EnumerateArray()
            .SelectMany(a => a.GetProperty("lots").EnumerateArray())
            .First(l => l.GetProperty("source").GetString() == "Manual");
        Assert.Equal(8m, updated.GetProperty("shares").GetDecimal());
        Assert.Equal(96m, updated.GetProperty("totalCost").GetDecimal());

        var missing = await _fx.Client.PutAsJsonAsync("/api/lots/999999", new
        {
            shares = 1m,
            totalCost = 1m,
            tradeDate = "2026-01-03",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }
}
