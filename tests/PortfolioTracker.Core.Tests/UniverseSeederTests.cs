using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Core.Tests;

public class UniverseSeederTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pt-test-{Guid.NewGuid():N}.db");
    private readonly Database _db;

    public UniverseSeederTests() => _db = new Database(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Embedded_seeds_load_and_look_sane()
    {
        var seeds = UniverseSeeder.LoadSeeds();
        Assert.Equal(5, seeds.Count);
        var sp500 = seeds.Single(s => s.Name == "S&P 500");
        Assert.InRange(sp500.Tickers.Count, 450, 520);
        Assert.Contains("AAPL", sp500.Tickers);
        Assert.Contains("BRK.B", sp500.Tickers);
        Assert.All(seeds, s => Assert.NotEmpty(s.Tickers));
        // no duplicate tickers within a universe (AddMembers would collapse
        // them silently; catching it here keeps the seed file honest)
        Assert.All(seeds, s => Assert.Equal(s.Tickers.Count, s.Tickers.Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }

    [Fact]
    public void Seeds_once_and_respects_user_deletions()
    {
        var universes = new UniverseRepository(_db);
        var state = new AppStateRepository(_db);

        UniverseSeeder.EnsureSeeded(universes, state);
        Assert.Equal(5, universes.GetAll().Count);
        Assert.True(universes.AnySeeded());
        Assert.NotEmpty(universes.GetMembers(universes.GetAll().Single(u => u.Name == "Mega-cap Tech").Id));

        // second run is a no-op
        UniverseSeeder.EnsureSeeded(universes, state);
        Assert.Equal(5, universes.GetAll().Count);

        // deleting a seeded universe sticks across restarts
        foreach (var u in universes.GetAll()) universes.Delete(u.Id);
        UniverseSeeder.EnsureSeeded(universes, state);
        Assert.Empty(universes.GetAll());
    }
}
