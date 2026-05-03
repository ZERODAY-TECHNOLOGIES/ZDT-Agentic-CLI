using System.Text.RegularExpressions;

namespace Zdtllm.Permissions;

internal sealed record ParsedRule(string ToolName, string? Specifier, Regex? Pattern);

public sealed class PermissionRuleParseException : Exception
{
    public PermissionRuleParseException(string message) : base(message) { }
}

internal static class RuleParser
{
    public static ParsedRule Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new PermissionRuleParseException("Permission rule cannot be empty.");

        var trimmed = raw.Trim();

        if (trimmed.EndsWith(')'))
        {
            var openIdx = trimmed.IndexOf('(');
            if (openIdx <= 0)
                throw new PermissionRuleParseException(
                    $"Malformed permission rule '{raw}': missing tool name before '('.");

            var toolName = trimmed[..openIdx];
            var specifier = trimmed[(openIdx + 1)..^1];

            if (!IsValidToolName(toolName))
                throw new PermissionRuleParseException(
                    $"Malformed permission rule '{raw}': invalid tool name '{toolName}'.");

            return new ParsedRule(toolName, specifier, GlobMatcher.Compile(specifier));
        }

        if (trimmed.Contains('(') || trimmed.Contains(')'))
            throw new PermissionRuleParseException(
                $"Malformed permission rule '{raw}': mismatched parentheses.");

        if (!IsValidToolName(trimmed))
            throw new PermissionRuleParseException(
                $"Malformed permission rule '{raw}': invalid tool name '{trimmed}'.");

        return new ParsedRule(trimmed, Specifier: null, Pattern: null);
    }

    private static bool IsValidToolName(string s)
    {
        if (s.Length == 0 || !char.IsAsciiLetter(s[0])) return false;
        foreach (var c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }
}
