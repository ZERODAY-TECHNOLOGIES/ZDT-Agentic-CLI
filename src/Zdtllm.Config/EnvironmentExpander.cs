using System.Text.RegularExpressions;

namespace Zdtllm.Config;

internal static partial class EnvironmentExpander
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex VarPattern();

    public static string Expand(string input, Func<string, string?> envRead)
    {
        if (input.Length == 0 || !input.Contains("${", StringComparison.Ordinal))
            return input;

        return VarPattern().Replace(input, match =>
        {
            var name = match.Groups[1].Value;
            return envRead(name) ?? string.Empty;
        });
    }

    public static string? ExpandNullable(string? input, Func<string, string?> envRead) =>
        input is null ? null : Expand(input, envRead);
}
