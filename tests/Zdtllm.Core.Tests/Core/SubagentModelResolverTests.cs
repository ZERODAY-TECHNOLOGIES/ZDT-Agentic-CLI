using System.Collections.Immutable;
using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

public sealed class SubagentModelResolverTests
{
    private static ImmutableDictionary<string, string> Aliases(params (string K, string V)[] items)
    {
        var b = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in items) b[k] = v;
        return b.ToImmutable();
    }

    [Fact]
    public void Returns_null_when_no_overrides_and_no_builtin_default_for_type()
    {
        // general-purpose has no builtin default — should fall through to "no override".
        var aliases = Aliases(("light", "qwen-fast"), ("medium", "qwen-mid"), ("heavy", "qwen-big"));
        var overrides = ImmutableDictionary<string, string>.Empty;

        var result = SubagentModelResolver.Resolve("general-purpose", aliases, overrides);

        result.Should().BeNull();
    }

    [Fact]
    public void Code_reviewer_default_routes_to_light_tier_when_alias_present()
    {
        var aliases = Aliases(("light", "qwen-fast"), ("medium", "qwen-mid"));
        var overrides = ImmutableDictionary<string, string>.Empty;

        var result = SubagentModelResolver.Resolve("code-reviewer", aliases, overrides);

        result.Should().Be("qwen-fast");
    }

    [Fact]
    public void Explore_default_also_routes_to_light_tier()
    {
        var aliases = Aliases(("light", "qwen-fast"), ("medium", "qwen-mid"));
        var overrides = ImmutableDictionary<string, string>.Empty;

        var result = SubagentModelResolver.Resolve("explore", aliases, overrides);

        result.Should().Be("qwen-fast");
    }

    [Fact]
    public void Builtin_default_is_skipped_when_alias_missing_from_models()
    {
        // User configured an aliasless setup with only "medium" present. The code-reviewer
        // builtin default points at "light" which doesn't exist — must NOT pass "light"
        // through as a literal id (LiteLLM would 404). Must return null so the runner
        // falls back to the parent.
        var aliases = Aliases(("medium", "qwen-mid"));
        var overrides = ImmutableDictionary<string, string>.Empty;

        var result = SubagentModelResolver.Resolve("code-reviewer", aliases, overrides);

        result.Should().BeNull();
    }

    [Fact]
    public void Explicit_override_alias_expands_through_models_map()
    {
        var aliases = Aliases(("light", "qwen-fast"), ("heavy", "glm-big"));
        var overrides = Aliases(("code-reviewer", "heavy"));

        var result = SubagentModelResolver.Resolve("code-reviewer", aliases, overrides);

        result.Should().Be("glm-big");
    }

    [Fact]
    public void Explicit_override_can_be_a_literal_model_id_when_not_an_alias()
    {
        // Escape hatch — value isn't in the alias map, treat as a literal model id.
        var aliases = Aliases(("light", "qwen-fast"));
        var overrides = Aliases(("code-reviewer", "deepseek-v3"));

        var result = SubagentModelResolver.Resolve("code-reviewer", aliases, overrides);

        result.Should().Be("deepseek-v3");
    }

    [Fact]
    public void Override_takes_precedence_over_builtin_default()
    {
        // Default for code-reviewer is "light" → qwen-fast. The user's override pins it to
        // medium → qwen-mid; that must win.
        var aliases = Aliases(("light", "qwen-fast"), ("medium", "qwen-mid"));
        var overrides = Aliases(("code-reviewer", "medium"));

        var result = SubagentModelResolver.Resolve("code-reviewer", aliases, overrides);

        result.Should().Be("qwen-mid");
    }

    [Fact]
    public void Custom_subagent_type_with_override_returns_the_override()
    {
        // Hypothetical user-defined type. No builtin default applies, but the explicit
        // override pins it.
        var aliases = Aliases(("medium", "qwen-mid"));
        var overrides = Aliases(("custom-archeologist", "medium"));

        var result = SubagentModelResolver.Resolve("custom-archeologist", aliases, overrides);

        result.Should().Be("qwen-mid");
    }

    [Fact]
    public void Empty_override_string_falls_through_to_builtin_default()
    {
        // A blank value in subagentModels (e.g. user typed "" by mistake) shouldn't override
        // — fall through as if the key wasn't there.
        var aliases = Aliases(("light", "qwen-fast"));
        var overrides = Aliases(("code-reviewer", ""));

        var result = SubagentModelResolver.Resolve("code-reviewer", aliases, overrides);

        result.Should().Be("qwen-fast");
    }

    [Fact]
    public void Small_fast_model_takes_precedence_over_builtin_default_for_code_reviewer()
    {
        // ZDT_SMALL_FAST_MODEL=foo with no subagentModels override — code-reviewer should
        // route to foo, not to the "light" tier from Models.
        var aliases = Aliases(("light", "qwen-fast"), ("medium", "qwen-mid"));
        var overrides = ImmutableDictionary<string, string>.Empty;

        var result = SubagentModelResolver.Resolve(
            "code-reviewer", aliases, overrides, smallFastModel: "small-fast-pin");

        result.Should().Be("small-fast-pin");
    }

    [Fact]
    public void Small_fast_model_also_applies_to_explore()
    {
        var aliases = Aliases(("light", "qwen-fast"));
        var overrides = ImmutableDictionary<string, string>.Empty;

        var result = SubagentModelResolver.Resolve(
            "explore", aliases, overrides, smallFastModel: "small-fast-pin");

        result.Should().Be("small-fast-pin");
    }

    [Fact]
    public void Small_fast_model_does_not_apply_to_general_purpose()
    {
        // general-purpose is not a "fast subagent" — ZDT_SMALL_FAST_MODEL must not affect it.
        var aliases = Aliases(("light", "qwen-fast"));
        var overrides = ImmutableDictionary<string, string>.Empty;

        var result = SubagentModelResolver.Resolve(
            "general-purpose", aliases, overrides, smallFastModel: "small-fast-pin");

        result.Should().BeNull();
    }

    [Fact]
    public void Explicit_subagent_override_still_wins_over_small_fast_env()
    {
        // The user explicitly pinned code-reviewer to medium in settings — that beats the
        // ZDT_SMALL_FAST_MODEL env var (env can't override an explicit user choice).
        var aliases = Aliases(("light", "qwen-fast"), ("medium", "qwen-mid"));
        var overrides = Aliases(("code-reviewer", "medium"));

        var result = SubagentModelResolver.Resolve(
            "code-reviewer", aliases, overrides, smallFastModel: "should-not-win");

        result.Should().Be("qwen-mid");
    }

    [Fact]
    public void Small_fast_value_is_alias_expanded_through_models_map()
    {
        // ZDT_SMALL_FAST_MODEL=light should resolve through the Models map instead of being
        // sent to LiteLLM as the literal string "light".
        var aliases = Aliases(("light", "qwen-fast"));
        var overrides = ImmutableDictionary<string, string>.Empty;

        var result = SubagentModelResolver.Resolve(
            "code-reviewer", aliases, overrides, smallFastModel: "light");

        result.Should().Be("qwen-fast");
    }
}
