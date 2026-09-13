namespace PortfolioTracker.Core.Tests;

public class DataDirectoryTests
{
    [Fact]
    public void Default_data_directory_remains_compatible()
        => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PortfolioTracker"), AppSettings.ResolveDataDirectory(null));

    [Fact]
    public void A_development_override_resolves_to_a_separate_absolute_directory()
        => Assert.Equal(Path.GetFullPath("sample-data"), AppSettings.ResolveDataDirectory("sample-data"));
}
