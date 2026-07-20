using System.Text.Json;
using PortfolioTracker.Core.Data;

namespace PortfolioTracker.Core.Portfolio;

/// Loads the seed universes shipped in the assembly exactly once per database.
/// The once-ever stamp lives in AppState, not "does a seeded universe exist" —
/// seeds are a starting point the user may freely rename or delete, and a
/// deleted seed must stay deleted across restarts.
public static class UniverseSeeder
{
    public const string SeededKey = "universes.seeded";

    public static void EnsureSeeded(UniverseRepository universes, AppStateRepository state)
    {
        if (state.Get(SeededKey) is not null) return;
        foreach (var seed in LoadSeeds())
        {
            var id = universes.Insert(seed.Name, isSeeded: true);
            universes.AddMembers(id, seed.Tickers);
        }
        state.Set(SeededKey, DateTimeOffset.Now.ToString("o"));
    }

    public record SeedUniverse(string Name, List<string> Tickers);
    private record SeedFile(List<SeedUniverse> Universes);

    public static List<SeedUniverse> LoadSeeds()
    {
        using var stream = typeof(UniverseSeeder).Assembly
            .GetManifestResourceStream("PortfolioTracker.Core.Seeds.universes.json")
            ?? throw new InvalidOperationException("Embedded seed universes are missing.");
        var file = JsonSerializer.Deserialize<SeedFile>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Seed universes JSON is unreadable.");
        return file.Universes;
    }
}
