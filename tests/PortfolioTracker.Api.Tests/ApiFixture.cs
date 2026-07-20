using System.Net.Http.Headers;
using PortfolioTracker.Api;

namespace PortfolioTracker.Api.Tests;

/// One live ApiHost on a random loopback port over a throwaway app-data dir —
/// the same wiring the shell uses, minus the window.
public sealed class ApiFixture : IAsyncLifetime
{
    public const string Token = "test-token";
    public string AppDataDir { get; } =
        Path.Combine(Path.GetTempPath(), $"pt-api-tests-{Guid.NewGuid():N}");
    public ApiHost Host { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public HttpClient AnonymousClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(AppDataDir);
        await Host.StartAsync(new ApiHostOptions { AppDataDir = AppDataDir, Token = Token });
        Client = new HttpClient { BaseAddress = Host.BaseUri };
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        AnonymousClient = new HttpClient { BaseAddress = Host.BaseUri };
    }

    public async Task DisposeAsync()
    {
        await Host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(AppDataDir, recursive: true); } catch (IOException) { }
    }
}
