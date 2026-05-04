using Zdtllm.Cli;

namespace Zdtllm.Core.Tests.Cli;

/// <summary>
/// SelfUpdate.ParseVersion is the only piece with deterministic, hermetic logic worth
/// unit-testing — the rest of self-update talks to GitHub or spawns a child process,
/// which we cover with manual / live verification rather than synthetic tests.
/// </summary>
public sealed class SelfUpdateVersionTests
{
    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("V0.2.5", "0.2.5")]      // capital V tolerated
    [InlineData("v1.0", "1.0.0")]         // build coerced to 0
    [InlineData("v0.1.0-rc1", "0.1.0")]   // pre-release suffix stripped
    [InlineData("v0.1.0+build.7", "0.1.0")] // build metadata stripped
    public void Parses_valid_release_tag_forms(string raw, string expected)
    {
        var parsed = SelfUpdate.ParseVersion(raw);
        parsed.Should().NotBeNull();
        parsed!.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    [InlineData(null)]
    public void Returns_null_for_garbage_input(string? raw)
    {
        SelfUpdate.ParseVersion(raw).Should().BeNull();
    }

    [Fact]
    public void Comparing_parsed_versions_uses_numeric_order()
    {
        var older = SelfUpdate.ParseVersion("v0.1.0")!;
        var newer = SelfUpdate.ParseVersion("v0.2.0")!;
        var patched = SelfUpdate.ParseVersion("v0.1.1")!;

        newer.Should().BeGreaterThan(older);
        patched.Should().BeGreaterThan(older);
        newer.Should().BeGreaterThan(patched);
    }

    [Fact]
    public void Same_version_with_different_prefixes_compare_equal()
    {
        SelfUpdate.ParseVersion("v0.1.0").Should().Be(SelfUpdate.ParseVersion("0.1.0"));
        SelfUpdate.ParseVersion("v0.1.0-rc2").Should().Be(SelfUpdate.ParseVersion("0.1.0"));
    }
}
