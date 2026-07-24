using System.Text;

namespace Zdtllm.Core;

/// <summary>
/// Assembles the project-memory string composed into the system prompt from a hierarchy of
/// <c>ZDTLLM.md</c> files — the user's <c>~/.zdtllm/ZDTLLM.md</c> plus every <c>ZDTLLM.md</c> from the
/// repository root down to the cwd (so a nested directory can add to, and override the emphasis of,
/// the repo-wide memory). Each file may pull in others with <c>@import &lt;path&gt;</c> (or a bare
/// <c>@path</c> on its own line); imports are resolved relative to the including file, depth-limited,
/// and cycle-guarded. Replaces the old "read only cwd/ZDTLLM.md" behaviour.
/// </summary>
public static class MemoryLoader
{
    private const string FileName = "ZDTLLM.md";
    private const int MaxImportDepth = 5;

    public static string? Load(string cwd, string? userHomeOverride = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cwd);

        var sources = new List<(string Label, string Path)>();

        var home = userHomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userMem = Path.Combine(home, ".zdtllm", FileName);
        if (File.Exists(userMem)) sources.Add(("user (~/.zdtllm/ZDTLLM.md)", userMem));

        foreach (var dir in AncestorsRootToCwd(cwd))
        {
            var p = Path.Combine(dir, FileName);
            if (File.Exists(p))
            {
                string label;
                try { label = $"project ({Path.GetRelativePath(cwd, p)})"; } catch { label = "project"; }
                sources.Add((label, p));
            }
        }

        if (sources.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var (label, path) in sources)
        {
            var content = ReadWithImports(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0).Trim();
            if (content.Length == 0) continue;
            if (sb.Length > 0) sb.AppendLine();
            if (sources.Count > 1) sb.Append("# ").AppendLine(label);
            sb.AppendLine(content);
        }
        var result = sb.ToString().TrimEnd();
        return result.Length == 0 ? null : result;
    }

    /// <summary>Directories from the repository root down to (and including) the cwd. The root is the
    /// nearest ancestor containing a <c>.git</c>; without one we use only the cwd, so we never slurp
    /// an unrelated ZDTLLM.md from far up the tree.</summary>
    private static IReadOnlyList<string> AncestorsRootToCwd(string cwd)
    {
        var chain = new List<string>();
        string? repoRoot = null;
        for (var cur = new DirectoryInfo(Path.GetFullPath(cwd)); cur is not null; cur = cur.Parent)
        {
            chain.Add(cur.FullName);
            if (Directory.Exists(Path.Combine(cur.FullName, ".git"))) { repoRoot = cur.FullName; break; }
        }

        if (repoRoot is null) return new[] { Path.GetFullPath(cwd) };
        chain.Reverse(); // root → cwd
        return chain;
    }

    private static string ReadWithImports(string path, HashSet<string> visited, int depth)
    {
        var full = Path.GetFullPath(path);
        if (depth > MaxImportDepth || !visited.Add(full)) return string.Empty;

        string text;
        try { text = File.ReadAllText(full); } catch { return string.Empty; }

        var baseDir = Path.GetDirectoryName(full)!;
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.TrimStart();

            string? importPath = null;
            if (t.StartsWith("@import ", StringComparison.OrdinalIgnoreCase))
                importPath = t[8..].Trim().Trim('"', '\'');
            else if (t.Length > 1 && t[0] == '@' && !t.Contains(' '))
                importPath = t[1..].Trim();

            if (importPath is { Length: > 0 })
            {
                var ip = Path.IsPathRooted(importPath) ? importPath : Path.Combine(baseDir, importPath);
                if (File.Exists(ip))
                {
                    sb.AppendLine(ReadWithImports(ip, visited, depth + 1).TrimEnd());
                    continue;
                }
            }

            sb.AppendLine(line);
        }
        return sb.ToString();
    }
}
