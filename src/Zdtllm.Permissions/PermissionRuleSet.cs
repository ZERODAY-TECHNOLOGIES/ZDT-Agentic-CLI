using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Zdtllm.Permissions;

public enum PermissionDecision
{
    Allow,
    Ask,
    Deny,
}

public sealed class PermissionRuleSet
{
    private static readonly FrozenSet<string> PermissionRequiredTools =
        new[] { "Bash", "Edit", "Write", "WebFetch", "WebSearch", "Skill" }
            .ToFrozenSet(StringComparer.Ordinal);

    private readonly ImmutableArray<ParsedRule> _deny;
    private readonly ImmutableArray<ParsedRule> _ask;
    private readonly ImmutableArray<ParsedRule> _allow;

    private PermissionRuleSet(
        ImmutableArray<ParsedRule> deny,
        ImmutableArray<ParsedRule> ask,
        ImmutableArray<ParsedRule> allow)
    {
        _deny = deny;
        _ask = ask;
        _allow = allow;
    }

    public static PermissionRuleSet Empty { get; } = new(
        ImmutableArray<ParsedRule>.Empty,
        ImmutableArray<ParsedRule>.Empty,
        ImmutableArray<ParsedRule>.Empty);

    public static PermissionRuleSet Build(
        IReadOnlyList<string> allow,
        IReadOnlyList<string> ask,
        IReadOnlyList<string> deny)
    {
        ArgumentNullException.ThrowIfNull(allow);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(deny);
        return new PermissionRuleSet(ParseAll(deny), ParseAll(ask), ParseAll(allow));
    }

    public PermissionDecision Evaluate(string toolName, string? specifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);

        if (FirstMatch(_deny, toolName, specifier)) return PermissionDecision.Deny;
        if (FirstMatch(_ask, toolName, specifier)) return PermissionDecision.Ask;
        if (FirstMatch(_allow, toolName, specifier)) return PermissionDecision.Allow;

        return RequiresPermission(toolName)
            ? PermissionDecision.Ask
            : PermissionDecision.Allow;
    }

    public static bool RequiresPermission(string toolName) =>
        PermissionRequiredTools.Contains(toolName);

    private static bool FirstMatch(ImmutableArray<ParsedRule> rules, string toolName, string? specifier)
    {
        foreach (var rule in rules)
        {
            if (!string.Equals(rule.ToolName, toolName, StringComparison.Ordinal))
                continue;
            if (rule.Pattern is null)
                return true;
            if (specifier is null)
                continue;
            if (rule.Pattern.IsMatch(specifier))
                return true;
        }
        return false;
    }

    private static ImmutableArray<ParsedRule> ParseAll(IReadOnlyList<string> rules)
    {
        if (rules.Count == 0) return ImmutableArray<ParsedRule>.Empty;
        var b = ImmutableArray.CreateBuilder<ParsedRule>(rules.Count);
        foreach (var r in rules) b.Add(RuleParser.Parse(r));
        return b.ToImmutable();
    }
}
