using System.Text.RegularExpressions;

namespace Zdtllm.Core.Workflows;

/// <summary>
/// Tiny <c>{{name}}</c> substitution used in workflow prompts. Names resolve against a context of
/// input args, the current fan-out <c>item</c>, and prior phases' <c>{{Title.results}}</c>. Unknown
/// placeholders are left untouched (so a typo is visible in the prompt rather than silently blank).
/// Pure and side-effect free — unit-testable on its own.
/// </summary>
public static partial class WorkflowTemplate
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_][A-Za-z0-9_.]*)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    public static string Resolve(string template, IReadOnlyDictionary<string, string> context)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        return PlaceholderRegex().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            return context.TryGetValue(key, out var value) ? value : m.Value; // leave unknown as-is
        });
    }

    /// <summary>The distinct placeholder names referenced in <paramref name="template"/>.</summary>
    public static IReadOnlyList<string> Placeholders(string template)
    {
        if (string.IsNullOrEmpty(template)) return Array.Empty<string>();
        return PlaceholderRegex().Matches(template)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
