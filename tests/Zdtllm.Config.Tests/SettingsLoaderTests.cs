using Zdtllm.Config;

namespace Zdtllm.Config.Tests;

public sealed class SettingsLoaderTests : IDisposable
{
    private readonly string _tempRoot;

    public SettingsLoaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "zdtllm-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private string ProjectDir
    {
        get
        {
            var p = Path.Combine(_tempRoot, "project");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    private string UserConfigPath => Path.Combine(_tempRoot, "user-settings.json");

    private void WriteUser(string json) => File.WriteAllText(UserConfigPath, json);

    private void WriteProject(string json)
    {
        var dir = Path.Combine(ProjectDir, ".zdtllm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
    }

    private void WriteLocal(string json)
    {
        var dir = Path.Combine(ProjectDir, ".zdtllm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.local.json"), json);
    }

    private SettingsLoadOptions Options(IDictionary<string, string?>? env = null)
    {
        var lookup = env ?? new Dictionary<string, string?>();
        return new SettingsLoadOptions
        {
            UserConfigPath = UserConfigPath,
            EnvironmentReader = name => lookup.TryGetValue(name, out var v) ? v : null,
        };
    }

    [Fact]
    public void Returns_Empty_when_no_files_exist()
    {
        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());
        s.Should().Be(EffectiveSettings.Empty);
    }

    [Fact]
    public void Loads_user_only_when_no_project_or_local()
    {
        WriteUser("""{ "model": "heavy" }""");

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.Model.Should().Be("heavy");
    }

    [Fact]
    public void Loads_project_only_when_no_user_or_local()
    {
        WriteProject("""{ "model": "medium" }""");

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.Model.Should().Be("medium");
    }

    [Fact]
    public void Project_overrides_user_for_scalars()
    {
        WriteUser("""{ "model": "light" }""");
        WriteProject("""{ "model": "heavy" }""");

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.Model.Should().Be("heavy");
    }

    [Fact]
    public void Local_overrides_project_and_user_for_scalars()
    {
        WriteUser("""{ "model": "light" }""");
        WriteProject("""{ "model": "medium" }""");
        WriteLocal("""{ "model": "heavy" }""");

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.Model.Should().Be("heavy");
    }

    [Fact]
    public void Permission_arrays_concat_and_dedup_across_scopes()
    {
        WriteUser("""
        {
          "permissions": { "allow": ["Read", "Bash(git status *)"] }
        }
        """);
        WriteProject("""
        {
          "permissions": { "allow": ["Bash(git diff *)", "Read"] }
        }
        """);

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.Permissions.Allow.Should().Equal("Read", "Bash(git status *)", "Bash(git diff *)");
    }

    [Fact]
    public void Empty_objects_yield_Empty_subsections()
    {
        WriteUser("{}");
        WriteProject("{}");

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.Model.Should().BeNull();
        s.Permissions.Should().Be(PermissionsSettings.Empty);
        s.LiteLLM.Should().Be(LiteLLMSettings.Empty);
    }

    [Fact]
    public void Expands_env_vars_in_apiKey()
    {
        WriteProject("""
        {
          "litellm": { "apiKey": "${ZDTLLM_API_KEY}" }
        }
        """);

        var env = new Dictionary<string, string?> { ["ZDTLLM_API_KEY"] = "sk-secret-123" };
        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options(env));

        s.LiteLLM.ApiKey.Should().Be("sk-secret-123");
    }

    [Fact]
    public void Missing_env_var_expands_to_empty_string()
    {
        WriteProject("""
        {
          "litellm": { "apiKey": "${ZDTLLM_NOT_SET}" }
        }
        """);

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.LiteLLM.ApiKey.Should().Be(string.Empty);
    }

    [Fact]
    public void Dict_values_merge_per_key_with_higher_winning()
    {
        WriteUser("""
        {
          "litellm": {
            "models": { "light": "qwen-flash", "medium": "qwen-mid" }
          }
        }
        """);
        WriteProject("""
        {
          "litellm": {
            "models": { "medium": "qwen-coder", "heavy": "qwen-max" }
          }
        }
        """);

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.LiteLLM.Models.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["light"] = "qwen-flash",
            ["medium"] = "qwen-coder",
            ["heavy"] = "qwen-max",
        });
    }

    [Fact]
    public void Throws_SettingsLoadException_on_malformed_json()
    {
        WriteProject("{ this is not json");

        var act = () => SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        act.Should().Throw<SettingsLoadException>()
            .WithMessage("*settings.json*");
    }

    [Fact]
    public void Allows_trailing_commas_and_comments()
    {
        WriteProject("""
        {
          // hand-edited config
          "model": "heavy",
          "litellm": {
            "baseUrl": "http://localhost:4000",
          },
        }
        """);

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.Model.Should().Be("heavy");
        s.LiteLLM.BaseUrl.Should().Be("http://localhost:4000");
    }
}
