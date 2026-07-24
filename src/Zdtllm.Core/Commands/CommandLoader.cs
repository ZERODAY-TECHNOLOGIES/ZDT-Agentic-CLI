using System.Text.RegularExpressions;
using Zdtllm.Core.Repl;

namespace Zdtllm.Core.Commands;

/// <summary>
/// A user-defined slash command loaded from <c>.zdtllm/commands/&lt;name&gt;.md</c>. Invoking
/// <c>/&lt;name&gt; args…</c> expands <see cref="Body"/> (substituting <c>$ARGUMENTS</c> and
/// <c>$1…$9</c>) and runs it as a normal turn. Mirrors claude-cli's custom commands.
/// </summary>
public sealed record CustomCommand(string Name, string Description, string? ArgumentHint, string Body)
{
    /// <summary>Expand the command body for the given argument string: <c>$ARGUMENTS</c> → the whole
    /// arg string, <c>$1…$9</c> → the whitespace-split positional tokens (missing ones → empty).</summary>
    public string Expand(string args)
    {
        var body = Body.Replace("$ARGUMENTS", args.Trim(), StringComparison.Ordinal);
        var tokens = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= 9; i++)
            body = body.Replace("$" + i, i - 1 < tokens.Length ? tokens[i - 1] : string.Empty, StringComparison.Ordinal);
        return body.Trim();
    }
}

/// <summary>
/// Discovers <c>*.md</c> command files under the user (<c>~/.zdtllm/commands/</c>) and project
/// (<c>&lt;cwd&gt;/.zdtllm/commands/</c>) roots. The file name (sans extension) is the command name;
/// project commands override user commands. Names that collide with a built-in slash command, or
/// are otherwise invalid, are skipped so a stray file can't shadow <c>/help</c>. Frontmatter is
/// optional (a plain <c>--- description: … ---</c> block); parsed line-by-line so Core needn't pull
/// in a YAML dependency.
/// </summary>
public sealed class CommandLoader
{
    private static readonly Regex ValidName = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);
    private static readonly Regex Frontmatter =
        new(@"\A---\s*\r?\n(.*?)\r?\n---\s*\r?\n(.*)\z", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly HashSet<string> BuiltinNames =
        SlashCommandCatalog.All.Select(c => c.Name.TrimStart('/')).ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<CustomCommand> Discover(string cwd, string? userRootOverride = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cwd);
        var byName = new Dictionary<string, CustomCommand>(StringComparer.Ordinal);
        DiscoverFrom(userRootOverride ?? DefaultUserRoot(), byName);
        DiscoverFrom(Path.Combine(cwd, ".zdtllm", "commands"), byName);
        return byName.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    public static string DefaultUserRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zdtllm", "commands");

    private static void DiscoverFrom(string root, Dictionary<string, CustomCommand> sink)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (!ValidName.IsMatch(name) || BuiltinNames.Contains(name)) continue;
            try
            {
                var cmd = LoadOne(file, name);
                if (cmd is not null) sink[name] = cmd;
            }
            catch { /* malformed command file — skip it, never fail the whole agent */ }
        }
    }

    private static CustomCommand? LoadOne(string path, string name)
    {
        var text = File.ReadAllText(path);
        string body = text;
        string? description = null;
        string? argHint = null;

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
                if (key == "description") description = val;
                else if (key is "argument-hint" or "argumenthint") argHint = val;
            }
        }

        body = body.Trim();
        if (body.Length == 0) return null;
        description ??= $"custom command (/{name})";
        return new CustomCommand(name, description, argHint, body);
    }
}
