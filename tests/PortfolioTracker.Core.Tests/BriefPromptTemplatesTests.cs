using PortfolioTracker.Core;
using PortfolioTracker.Core.Research;

namespace PortfolioTracker.Core.Tests;

public class BriefPromptTemplatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pt-prompts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void First_load_seeds_all_template_files()
    {
        var templates = new BriefPromptTemplates(_dir);
        var system = templates.Load(BriefPromptTemplates.SystemFile);

        Assert.Contains("ONLY figures present in the supplied DATA block", system);
        foreach (var name in BriefPromptTemplates.Defaults.Keys)
            Assert.True(File.Exists(Path.Combine(templates.Dir, name)), $"{name} not seeded");
    }

    [Fact]
    public void Edits_hot_reload_without_restart_and_reset_restores_defaults()
    {
        var templates = new BriefPromptTemplates(_dir);
        templates.Seed(overwrite: false);
        var path = Path.Combine(templates.Dir, "section-key-numbers.md");

        File.WriteAllText(path, "My custom key-numbers instructions.");
        Assert.Equal("My custom key-numbers instructions.", templates.Load("section-key-numbers.md"));

        templates.Seed(overwrite: true);   // "Reset to defaults"
        Assert.Equal(BriefPromptTemplates.Defaults["section-key-numbers.md"],
            templates.Load("section-key-numbers.md"));
    }

    [Fact]
    public void Empty_or_missing_file_falls_back_to_the_embedded_default()
    {
        var templates = new BriefPromptTemplates(_dir);
        templates.Seed(overwrite: false);
        File.WriteAllText(Path.Combine(templates.Dir, BriefPromptTemplates.RepairFile), "   ");
        var repair = templates.Load(BriefPromptTemplates.RepairFile);
        Assert.Contains("{{VIOLATIONS}}", repair);
    }

    [Fact]
    public void Section_templates_carry_a_format_contract_and_example()
    {
        foreach (var name in BriefPromptTemplates.SectionFiles)
        {
            var text = BriefPromptTemplates.Defaults[name];
            Assert.Contains("Format contract:", text);
            Assert.Contains("Example", text);
        }
    }
}

/// The secrets checklist, as a test: provider credentials exist only in the
/// git-ignored app-data settings file, under these exact key names, with no
/// baked-in defaults anywhere in the tree.
public class SecretsHygieneTests
{
    [Fact]
    public void Fresh_settings_contain_no_credentials()
    {
        var s = new AppSettings();
        Assert.Null(s.FinnhubApiKey);
        Assert.Null(s.AnthropicApiKey);
        Assert.Null(s.OpenWebUiBaseUrl);
        Assert.Null(s.OpenWebUiApiKey);
        Assert.Null(s.OpenWebUiModel);
    }

    [Fact]
    public void Provider_config_serializes_under_the_exact_contract_key_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pt-settings-{Guid.NewGuid():N}");
        try
        {
            var s = AppSettings.Load(dir);
            s.AiProvider = "openwebui";
            s.OpenWebUiBaseUrl = "https://unit.test";
            s.OpenWebUiApiKey = "unit-test-key";
            s.OpenWebUiModel = "unit-test-model";
            s.Save();

            var json = File.ReadAllText(Path.Combine(dir, "appsettings.json"));
            Assert.Contains("\"AI_VISION_PROVIDER\"", json);
            Assert.Contains("\"OPENWEBUI_BASE_URL\"", json);
            Assert.Contains("\"OPENWEBUI_API_KEY\"", json);
            Assert.Contains("\"OPENWEBUI_MODEL\"", json);

            var reloaded = AppSettings.Load(dir);
            Assert.Equal("openwebui", reloaded.AiProvider);
            Assert.Equal("unit-test-key", reloaded.OpenWebUiApiKey);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Settings_written_before_the_provider_key_existed_migrate_sensibly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pt-settings-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "appsettings.json");

            File.WriteAllText(file, """{ "AnthropicApiKey": "sk-test" }""");
            Assert.Equal("anthropic", AppSettings.Load(dir).AiProvider);

            File.WriteAllText(file, """{ "FinnhubApiKey": "fh-test" }""");
            Assert.Equal("disabled", AppSettings.Load(dir).AiProvider);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void No_git_tracked_source_file_hardcodes_a_bearer_credential()
    {
        // walk up from the test assembly to the repo root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortfolioTracker.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var offenders = new List<string>();
        foreach (var root in new[] { "src", Path.Combine("frontend", "src") })
        {
            var path = Path.Combine(dir!.FullName, root);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
                    continue;
                var ext = Path.GetExtension(file);
                if (ext is not (".cs" or ".ts" or ".tsx" or ".json" or ".md")) continue;
                var text = File.ReadAllText(file);
                // real key material, not key NAMES: Anthropic keys and OpenWebUI
                // JWT-style bearer tokens have recognizable prefixes
                if (text.Contains("sk-ant-api") || text.Contains("Bearer eyJ"))
                    offenders.Add(file);
            }
        }
        Assert.True(offenders.Count == 0, "credential-like literals in: " + string.Join(", ", offenders));
    }
}
