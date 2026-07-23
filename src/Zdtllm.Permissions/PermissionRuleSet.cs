using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

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

    /// <summary>Number of rules in each precedence bucket. Useful for /permissions UI.</summary>
    public (int deny, int ask, int allow) RuleCounts =>
        (_deny.Length, _ask.Length, _allow.Length);

    /// <summary>
    /// Human-readable rule strings ("Bash", "Bash(git *)") for each precedence bucket,
    /// in the order they were configured. Used by /permissions to populate a table.
    /// </summary>
    public IReadOnlyList<string> AllowRules => _allow.Select(FormatRule).ToList();
    public IReadOnlyList<string> AskRules => _ask.Select(FormatRule).ToList();
    public IReadOnlyList<string> DenyRules => _deny.Select(FormatRule).ToList();

    private static string FormatRule(ParsedRule r) =>
        r.Specifier is null ? r.ToolName : $"{r.ToolName}({r.Specifier})";

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

    /// <summary>
    /// Bash-aware evaluation. A raw <c>Bash</c> specifier is the ENTIRE command line, so matching it
    /// against a single glob rule lets a chained command ride along on a narrow allowance
    /// (<c>Bash(git diff *)</c> would otherwise permit <c>git diff &amp;&amp; rm -rf /</c>). Instead we
    /// decompose the command into its sub-commands and require EVERY one to be independently allowed:
    /// the decision is the most restrictive across segments (Deny wins, then Ask, else Allow). A
    /// command that embeds another via <c>$(...)</c>/backticks is never auto-allowed.
    /// </summary>
    public PermissionDecision EvaluateBash(string command)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);

        // A deny rule authored against the whole command line still applies.
        if (FirstMatch(_deny, "Bash", command)) return PermissionDecision.Deny;

        var worst = PermissionDecision.Allow;
        foreach (var segment in BashCommandDecomposer.Decompose(command))
        {
            var d = Evaluate("Bash", segment);
            if (d == PermissionDecision.Deny) return PermissionDecision.Deny;
            if (d == PermissionDecision.Ask) worst = PermissionDecision.Ask;
        }

        if (worst == PermissionDecision.Allow && BashCommandDecomposer.HasCommandSubstitution(command))
            worst = PermissionDecision.Ask;

        return worst;
    }

    /// <summary>
    /// Return a new rule set with one extra allow rule that matches <paramref name="specifier"/>
    /// EXACTLY (no glob expansion). Backs the interactive "yes, don't ask again" choice: it grants a
    /// session-scoped allowance for the precise command/path the user just approved, without the
    /// broadening a wildcard rule would introduce. A null specifier grants the whole tool.
    /// </summary>
    public PermissionRuleSet WithAllowExact(string toolName, string? specifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        var rule = specifier is null
            ? new ParsedRule(toolName, Specifier: null, Pattern: null)
            : new ParsedRule(toolName, specifier, LiteralRegex(specifier));
        return new PermissionRuleSet(_deny, _ask, _allow.Add(rule));
    }

    private static Regex LiteralRegex(string literal) =>
        new("^" + Regex.Escape(literal) + "$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

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
