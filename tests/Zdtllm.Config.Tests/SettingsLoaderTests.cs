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
    public void Subagent_models_load_from_litellm_section()
    {
        WriteUser("""
        {
          "litellm": {
            "models": { "light": "qwen-fast", "medium": "qwen-mid" },
            "subagentModels": {
              "code-reviewer": "light",
              "explore": "qwen-fast"
            }
          }
        }
        """);

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.LiteLLM.SubagentModels.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["code-reviewer"] = "light",
            ["explore"] = "qwen-fast",
        });
    }

    [Fact]
    public void Subagent_models_default_to_empty_when_unset()
    {
        WriteUser("""
        {
          "litellm": {
            "models": { "medium": "qwen-mid" }
          }
        }
        """);

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.LiteLLM.SubagentModels.Should().BeEmpty();
    }

    [Fact]
    public void Env_default_model_vars_populate_light_medium_heavy_aliases()
    {
        // Empty settings.json — the env layer alone provides the alias map.
        var env = new Dictionary<string, string?>
        {
            ["ZDT_DEFAULT_HEAVY_MODEL"]  = "heavy-from-env",
            ["ZDT_DEFAULT_MEDIUM_MODEL"] = "medium-from-env",
            ["ZDT_DEFAULT_LIGHT_MODEL"]  = "light-from-env",
        };

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options(env));

        s.LiteLLM.Models.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["heavy"]  = "heavy-from-env",
            ["medium"] = "medium-from-env",
            ["light"]  = "light-from-env",
        });
    }

    [Fact]
    public void Env_default_model_vars_override_same_tier_in_settings_json()
    {
        // Settings pins light → A; env pins light → B; env wins because it's the highest
        // precedence layer below CLI args.
        WriteUser("""
        {
          "litellm": {
            "models": { "light": "settings-light" }
          }
        }
        """);
        var env = new Dictionary<string, string?>
        {
            ["ZDT_DEFAULT_LIGHT_MODEL"] = "env-light-wins",
        };

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options(env));

        s.LiteLLM.Models["light"].Should().Be("env-light-wins");
    }

    [Fact]
    public void Env_layer_does_not_disturb_unrelated_tiers_or_extra_aliases()
    {
        WriteUser("""
        {
          "litellm": {
            "models": { "light": "qwen-fast", "medium": "qwen-mid", "custom": "x" }
          }
        }
        """);
        var env = new Dictionary<string, string?>
        {
            ["ZDT_DEFAULT_HEAVY_MODEL"] = "heavy-from-env",
        };

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options(env));

        s.LiteLLM.Models.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["light"]  = "qwen-fast",
            ["medium"] = "qwen-mid",
            ["custom"] = "x",
            ["heavy"]  = "heavy-from-env",
        });
    }

    [Fact]
    public void Env_small_fast_model_populates_litellm_smallFastModel()
    {
        var env = new Dictionary<string, string?>
        {
            ["ZDT_SMALL_FAST_MODEL"] = "fast-subagent-model",
        };

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options(env));

        s.LiteLLM.SmallFastModel.Should().Be("fast-subagent-model");
    }

    [Fact]
    public void Env_layer_no_op_when_no_zdt_vars_present()
    {
        // Settings file alone — no env vars. SmallFastModel stays null and Models matches
        // exactly what settings.json declared (no phantom tier entries).
        WriteUser("""
        {
          "litellm": {
            "models": { "medium": "qwen-mid" }
          }
        }
        """);

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.LiteLLM.SmallFastModel.Should().BeNull();
        s.LiteLLM.Models.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["medium"] = "qwen-mid",
        });
    }

    [Fact]
    public void Env_empty_string_value_is_ignored()
    {
        // export ZDT_DEFAULT_LIGHT_MODEL= (intentional unset via empty value) — must not
        // overwrite the settings entry with an empty string that would later 404 at LiteLLM.
        WriteUser("""
        {
          "litellm": {
            "models": { "light": "settings-light" }
          }
        }
        """);
        var env = new Dictionary<string, string?>
        {
            ["ZDT_DEFAULT_LIGHT_MODEL"] = "",
        };

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options(env));

        // Settings entry survives; empty env doesn't blank it.
        s.LiteLLM.Models["light"].Should().Be("settings-light");
    }

    [Fact]
    public void Subagent_models_merge_per_key_with_higher_layer_winning()
    {
        WriteUser("""
        {
          "litellm": {
            "subagentModels": { "code-reviewer": "light", "explore": "light" }
          }
        }
        """);
        WriteProject("""
        {
          "litellm": {
            "subagentModels": { "code-reviewer": "heavy" }
          }
        }
        """);

        var s = SettingsLoader.LoadEffectiveSettings(ProjectDir, Options());

        s.LiteLLM.SubagentModels.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["code-reviewer"] = "heavy",
            ["explore"] = "light",
        });
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
