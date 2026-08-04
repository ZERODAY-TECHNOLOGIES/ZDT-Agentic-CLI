using System.Text;
using System.Text.RegularExpressions;

namespace Zdtllm.Core.Agents;

/// <summary>
/// Discovers project subagents from <c>*.md</c> files under the user (<c>~/.zdtllm/agents/</c>) and
/// project (<c>&lt;cwd&gt;/.zdtllm/agents/</c>) roots. The file name (sans extension) is the fallback
/// subagent_type; project agents override user agents of the same name. Frontmatter is optional and
/// parsed line-by-line (no YAML dependency), exactly like <c>CommandLoader</c>:
///
/// <code>
/// ---
/// name: db-migrator
/// description: Writes and runs SQL migrations
/// tools: Read, Edit, Bash
/// model: inherit
/// ---
/// You are the db-migrator subagent. …
/// </code>
///
/// A malformed file is skipped, never fatal — one bad agent can't stop zdt from starting.
/// </summary>
public sealed class AgentDefinitionLoader
{
    private static readonly Regex ValidName = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);
    private static readonly Regex Frontmatter =
        new(@"\A---\s*\r?\n(.*?)\r?\n---\s*\r?\n(.*)\z", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Frontmatter values that mean "no restriction / inherit" rather than a real list/id.</summary>
    private static readonly HashSet<string> AllToolsSentinels =
        new(StringComparer.OrdinalIgnoreCase) { "all", "*", "any", "inherit" };
    private static readonly HashSet<string> InheritModelSentinels =
        new(StringComparer.OrdinalIgnoreCase) { "inherit", "parent", "default", "-", "" };

    public IReadOnlyList<AgentDefinition> Discover(string cwd, string? userRootOverride = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cwd);
        var byName = new Dictionary<string, AgentDefinition>(StringComparer.Ordinal);
        DiscoverFrom(userRootOverride ?? DefaultUserRoot(), byName);
        DiscoverFrom(ProjectRoot(cwd), byName);
        return byName.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    public static string DefaultUserRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zdtllm", "agents");

    public static string ProjectRoot(string cwd) => Path.Combine(cwd, ".zdtllm", "agents");

    private static void DiscoverFrom(string root, Dictionary<string, AgentDefinition> sink)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (!ValidName.IsMatch(name)) continue;
            try
            {
                var def = LoadOne(file, name);
                if (def is not null) sink[def.Name] = def;
            }
            catch { /* malformed agent file — skip it, never fail the whole agent */ }
        }
    }

    private static AgentDefinition? LoadOne(string path, string fileName)
    {
        var text = File.ReadAllText(path);
        string body = text;
        string? name = null;
        string? description = null;
        string? toolsRaw = null;
        string? model = null;

        var m = Frontmatter.Match(text);
        if (m.Success)
        {
            body = m.Groups[2].Value;
            foreach (var raw in m.Groups[1].Value.Split('\n'))
            {
                var l = raw.Trim();
                var colon = l.IndexOf(':');
                if (colon <= 0) continue;
                var key = l[..colon].Trim().ToLowerInvariant();
                var val = l[(colon + 1)..].Trim().Trim('"', '\'');
                switch (key)
                {
                    case "name": name = val; break;
                    case "description": description = val; break;
                    case "tools": toolsRaw = val; break;
                    case "model": model = val; break;
                }
            }
        }

        // A frontmatter name wins over the filename, but must still be a valid slug.
        if (!string.IsNullOrEmpty(name) && ValidName.IsMatch(name.ToLowerInvariant()))
            name = name.ToLowerInvariant();
        else
            name = fileName;

        body = body.Trim();
        description = string.IsNullOrWhiteSpace(description) ? $"project subagent ({name})" : description!;
        if (body.Length == 0) body = DefaultSystemPrompt(name!, description);

        return new AgentDefinition(
            Name: name!,
            Description: description,
            AllowedTools: ParseTools(toolsRaw),
            SystemPrompt: body,
            Model: NormaliseModel(model));
    }

    /// <summary>
    /// Parse the <c>tools:</c> value. Comma- and/or whitespace-separated tool names. The sentinels
    /// <c>all</c>/<c>*</c>/<c>any</c>/<c>inherit</c> (and an empty/missing value) mean "no restriction"
    /// → returns null, which SubagentRunner treats as the general-purpose (all-tools) profile.
    /// </summary>
    internal static IReadOnlySet<string>? ParseTools(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (AllToolsSentinels.Contains(trimmed)) return null;

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in trimmed.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (AllToolsSentinels.Contains(token)) return null; // "all" anywhere in the list wins
            set.Add(token);
        }
        return set.Count == 0 ? null : set;
    }

    /// <summary>Normalise the <c>model:</c> value: inherit-sentinels → null (run on the parent's model).</summary>
    internal static string? NormaliseModel(string? raw)
    {
        var v = raw?.Trim();
        return string.IsNullOrEmpty(v) || InheritModelSentinels.Contains(v) ? null : v;
    }

    /// <summary>A serviceable default system prompt when a definition ships no body.</summary>
    internal static string DefaultSystemPrompt(string name, string description) =>
        $"You are the '{name}' subagent: {description}. You were dispatched by an orchestrator and have " +
        "your own fresh context — you do not see the parent conversation. Complete the task autonomously " +
        "with the tools available to you, then return a concise report of what you did and the result. " +
        "Be specific and cite file paths / line numbers where relevant.";

    /// <summary>
    /// Render an <see cref="AgentDefinition"/> back to the on-disk <c>.md</c> format. Shared with the
    /// team-mode wizard so what it writes round-trips cleanly through <see cref="LoadOne"/>.
    /// </summary>
    public static string ToMarkdown(AgentDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        var tools = def.AllowedTools is null || def.AllowedTools.Count == 0
            ? "all"
            : string.Join(", ", def.AllowedTools.OrderBy(t => t, StringComparer.Ordinal));

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(def.Name).Append('\n');
        sb.Append("description: ").Append(def.Description).Append('\n');
        sb.Append("tools: ").Append(tools).Append('\n');
        sb.Append("model: ").Append(def.Model ?? "inherit").Append('\n');
        sb.Append("---\n\n");
        sb.Append(def.SystemPrompt.Trim()).Append('\n');
        return sb.ToString();
    }
}
