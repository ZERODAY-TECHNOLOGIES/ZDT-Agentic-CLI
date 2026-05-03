using System.Text.RegularExpressions;

namespace Zdtllm.Permissions;

internal static class GlobMatcher
{
    private const RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline;

    public static Regex Compile(string globPattern)
    {
        var escaped = Regex.Escape(globPattern);
        var regex = "^" + escaped.Replace(@"\*", ".*") + "$";
        return new Regex(regex, Options);
    }

    public static bool IsMatch(string globPattern, string input) =>
        Compile(globPattern).IsMatch(input);
}
