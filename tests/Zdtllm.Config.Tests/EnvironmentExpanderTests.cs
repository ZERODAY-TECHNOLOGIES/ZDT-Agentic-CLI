using Zdtllm.Config;

namespace Zdtllm.Config.Tests;

public sealed class EnvironmentExpanderTests
{
    [Fact]
    public void Replaces_known_var()
    {
        var result = EnvironmentExpander.Expand("${FOO}", n => n == "FOO" ? "bar" : null);
        result.Should().Be("bar");
    }

    [Fact]
    public void Returns_empty_for_missing_var()
    {
        var result = EnvironmentExpander.Expand("${MISSING}", _ => null);
        result.Should().Be(string.Empty);
    }

    [Fact]
    public void Returns_input_unchanged_when_no_pattern_present()
    {
        var result = EnvironmentExpander.Expand("plain string $no_braces", _ => "x");
        result.Should().Be("plain string $no_braces");
    }

    [Fact]
    public void Replaces_multiple_vars_including_repeats()
    {
        var env = new Dictionary<string, string?> { ["A"] = "1", ["B"] = "2" };
        var result = EnvironmentExpander.Expand(
            "${A}-${B}-${A}",
            n => env.TryGetValue(n, out var v) ? v : null);
        result.Should().Be("1-2-1");
    }

    [Fact]
    public void Supports_underscore_and_digits_in_var_name()
    {
        var env = new Dictionary<string, string?> { ["MY_VAR_1"] = "ok" };
        var result = EnvironmentExpander.Expand(
            "prefix_${MY_VAR_1}_suffix",
            n => env.TryGetValue(n, out var v) ? v : null);
        result.Should().Be("prefix_ok_suffix");
    }

    [Fact]
    public void ExpandNullable_passes_null_through()
    {
        EnvironmentExpander.ExpandNullable(null, _ => "x").Should().BeNull();
    }

    [Fact]
    public void Empty_string_input_returns_empty()
    {
        EnvironmentExpander.Expand(string.Empty, _ => "x").Should().Be(string.Empty);
    }
}
