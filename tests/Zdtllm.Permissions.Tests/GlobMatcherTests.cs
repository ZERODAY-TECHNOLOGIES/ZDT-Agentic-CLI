using Zdtllm.Permissions;

namespace Zdtllm.Permissions.Tests;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("*", "anything")]
    [InlineData("*", "")]
    [InlineData("git diff *", "git diff --cached")]
    [InlineData("git diff *", "git diff HEAD~1 -- file.cs")]
    [InlineData("./secrets/**", "./secrets/aws/key.pem")]
    [InlineData("foo*bar", "fooXYZbar")]
    [InlineData("foo*bar", "foobar")]
    [InlineData("a*b*c", "axxxxbyc")]
    public void Matches_when_glob_admits_input(string pattern, string input)
    {
        GlobMatcher.IsMatch(pattern, input).Should().BeTrue();
    }

    [Theory]
    [InlineData("git diff *", "git push origin")]
    [InlineData("foo", "foobar")]
    [InlineData("foo*bar", "fooXYZbaz")]
    [InlineData("./secrets/**", "./public/file")]
    public void Does_not_match_when_glob_excludes_input(string pattern, string input)
    {
        GlobMatcher.IsMatch(pattern, input).Should().BeFalse();
    }

    [Fact]
    public void Special_regex_chars_are_treated_literally()
    {
        // '.', '(', ')', '+' are regex metachars but should be literal in glob.
        GlobMatcher.IsMatch("./.env", "./.env").Should().BeTrue();
        GlobMatcher.IsMatch("./.env", "_/_env").Should().BeFalse();
        GlobMatcher.IsMatch("a+b", "a+b").Should().BeTrue();
        GlobMatcher.IsMatch("a+b", "aab").Should().BeFalse();
    }

    [Fact]
    public void Empty_pattern_matches_only_empty_input()
    {
        GlobMatcher.IsMatch("", "").Should().BeTrue();
        GlobMatcher.IsMatch("", "x").Should().BeFalse();
    }
}
