namespace Zdtllm.Tools;

/// <summary>
/// Directories that content/file search (<see cref="GrepTool"/>, <see cref="GlobTool"/>) skip by
/// default. Two reasons: (1) VCS / build / IDE noise (.git, bin, obj, node_modules, …) buries real hits
/// and thrashes I/O; (2) — critically — zdt's OWN state dir <c>.zdtllm</c>, whose <c>sessions/*.jsonl</c>
/// grows with the conversation: a <c>**/*</c> Glob or a broad Grep pulls the giant session file into the
/// result, which inflates the NEXT request, which grows the session further — a feedback loop that blew
/// the model's context window (a single request hit ~975k tokens). Mirrors what ripgrep skips via
/// .gitignore. Shared so Grep and Glob can never drift out of sync.
/// </summary>
internal static class SearchExclusions
{
    public static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", ".vscode", "dist", ".zdtllm",
    };

    public static bool IsIgnoredDirName(string name) => IgnoredDirs.Contains(name);

    /// <summary>True if any segment of the (relative or absolute) path is an ignored directory.</summary>
    public static bool IsUnderIgnoredDir(string path)
    {
        foreach (var seg in path.Split('/', '\\'))
            if (seg.Length > 0 && IgnoredDirs.Contains(seg)) return true;
        return false;
    }
}
