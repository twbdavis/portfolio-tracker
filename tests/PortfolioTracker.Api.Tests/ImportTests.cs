using System.Net.Http.Json;
using System.Text.Json;

namespace PortfolioTracker.Api.Tests;

public class ImportTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public ImportTests(ApiFixture fx) => _fx = fx;

    private async Task<JsonElement> PreviewSampleAsync()
    {
        using var content = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync("chase-transactions-sample.csv");
        content.Add(new ByteArrayContent(bytes), "file", "chase-transactions-sample.csv");
        var resp = await _fx.Client.PostAsync("/api/import/preview", content);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Preview_then_commit_imports_the_sample_csv()
    {
        var preview = await PreviewSampleAsync();
        Assert.True(preview.GetProperty("tradeRows").GetInt32() > 0);
        var previewId = preview.GetProperty("previewId").GetString()!;
        var unknown = preview.GetProperty("unknownAccounts").EnumerateArray().ToList();
        Assert.NotEmpty(unknown);   // fresh db: the sample file's account is new

        // accept the auto-suggested decisions verbatim
        var commit = await _fx.Client.PostAsJsonAsync("/api/import/commit", new
        {
            previewId,
            newAccounts = Array.Empty<object>(),
        });
        commit.EnsureSuccessStatusCode();
        var result = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("rowsImported").GetInt32() > 0);
        Assert.NotEmpty(result.GetProperty("reconciliation").EnumerateArray());

        // batches list shows it; a re-import fully dedupes
        var batches = await _fx.Client.GetFromJsonAsync<JsonElement>("/api/import/batches");
        Assert.NotEmpty(batches.EnumerateArray());

        var preview2 = await PreviewSampleAsync();
        Assert.Empty(preview2.GetProperty("unknownAccounts").EnumerateArray());
        var commit2 = await _fx.Client.PostAsJsonAsync("/api/import/commit", new
        {
            previewId = preview2.GetProperty("previewId").GetString(),
            newAccounts = Array.Empty<object>(),
        });
        commit2.EnsureSuccessStatusCode();
        var result2 = await commit2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, result2.GetProperty("rowsImported").GetInt32());
        Assert.True(result2.GetProperty("rowsDeduped").GetInt32() > 0);
    }

    [Fact]
    public async Task Commit_with_stale_preview_id_is_404()
    {
        var resp = await _fx.Client.PostAsJsonAsync("/api/import/commit", new
        {
            previewId = "nope",
            newAccounts = Array.Empty<object>(),
        });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Header_mismatch_returns_the_actual_headers_for_remapping()
    {
        using var content = new MultipartFormDataContent();
        var csv = "Date,Symbol,Qty\n\"1/5/2026\",\"MU\",\"1\"";
        content.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "weird.csv");
        var resp = await _fx.Client.PostAsync("/api/import/preview", content);
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Symbol", body.GetProperty("actualHeaders").EnumerateArray().Select(h => h.GetString()));
        Assert.Contains("Ticker", body.GetProperty("requiredFields").EnumerateArray().Select(h => h.GetString()));
    }
}
