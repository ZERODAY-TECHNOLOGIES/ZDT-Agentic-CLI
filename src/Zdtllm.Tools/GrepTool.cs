using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Zdtllm.Tools;

public sealed class GrepTool : ITool
{
    private const int DefaultHeadLimit = 250;

    // Build-output / VCS / IDE directories that ripgrep would skip via .gitignore. Scanning them
    // buries real hits under compiled artefacts and thrashes I/O; prune them during enumeration.
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", ".vscode", "dist",
    };

    // Common ripgrep --type aliases → globs. Only the frequent ones; unknown types fall back to
    // "scan everything" so the tool never errors on an unmapped alias.
    private static readonly Dictionary<string, string[]> TypeGlobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = ["*.cs"], ["py"] = ["*.py"], ["js"] = ["*.js", "*.mjs", "*.cjs", "*.jsx"],
        ["ts"] = ["*.ts", "*.tsx"], ["rust"] = ["*.rs"], ["go"] = ["*.go"], ["java"] = ["*.java"],
        ["c"] = ["*.c", "*.h"], ["cpp"] = ["*.cpp", "*.hpp", "*.cc", "*.cxx", "*.hxx"],
        ["json"] = ["*.json"], ["yaml"] = ["*.yaml", "*.yml"], ["md"] = ["*.md", "*.markdown"],
        ["html"] = ["*.html", "*.htm"], ["css"] = ["*.css", "*.scss"], ["sh"] = ["*.sh", "*.bash"],
        ["rb"] = ["*.rb"], ["php"] = ["*.php"], ["xml"] = ["*.xml"], ["sql"] = ["*.sql"],
        ["toml"] = ["*.toml"], ["ps"] = ["*.ps1", "*.psm1"],
    };

    public ToolSchema Schema { get; } = new(
        Name: "Grep",
        Description: "Search file contents for a regex (.NET syntax). Skips .git/bin/obj/node_modules and binary files. Default output is matching file paths (\"files_with_matches\"); set output_mode to \"content\" for matching lines (with optional -A/-B/-C context) or \"count\" for per-file counts.",
        Parameters: JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["pattern"] = new { type = "string", description = ".NET regex pattern." },
                ["path"] = new { type = "string", description = "Directory or file to search (default: current working directory)." },
                ["glob"] = new { type = "string", description = "Optional file glob (e.g. \"*.cs\") that restricts which files are scanned." },
                ["type"] = new { type = "string", description = "Optional file-type alias (cs, py, js, ts, rust, go, java, json, yaml, md, …). Ignored if 'glob' is set." },
                ["output_mode"] = new { type = "string", description = "files_with_matches (default) | content | count." },
                ["head_limit"] = new { type = "integer", description = "Max number of result lines (default 250)." },
                ["multiline"] = new { type = "boolean", description = "Match across line boundaries ('.' matches newlines); reports each match's start line." },
                ["-i"] = new { type = "boolean", description = "Case-insensitive match." },
                ["-n"] = new { type = "boolean", description = "In content mode, prefix each line with its 1-based line number." },
                ["-A"] = new { type = "integer", description = "content mode: lines of context to show AFTER each match." },
                ["-B"] = new { type = "integer", description = "content mode: lines of context to show BEFORE each match." },
                ["-C"] = new { type = "integer", description = "content mode: lines of context to show before AND after (overrides -A/-B when larger)." },
            },
            ["required"] = new[] { "pattern" },
        }));

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("pattern", out var p) || p.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("Grep: missing or invalid 'pattern' parameter."));

        var pattern = p.GetString()!;
        var searchPath = args.TryGetProperty("path", out var sp) && sp.ValueKind == JsonValueKind.String
            ? sp.GetString()!
            : ctx.Cwd;
        var fullPath = Path.IsPathRooted(searchPath)
            ? searchPath
            : Path.GetFullPath(Path.Combine(ctx.Cwd, searchPath));

        var glob = args.TryGetProperty("glob", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
        var typeAlias = args.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : null;
        var outputMode = (args.TryGetProperty("output_mode", out var om) && om.ValueKind == JsonValueKind.String
            ? om.GetString()!
            : "files_with_matches").ToLowerInvariant();
        var caseInsensitive = TryGetBool(args, "-i");
        var lineNumbers = TryGetBool(args, "-n");
        var multiline = TryGetBool(args, "multiline");
        var headLimit = TryGetInt(args, "head_limit", DefaultHeadLimit);

        var ctxC = TryGetInt(args, "-C", 0);
        var after = Math.Max(ctxC, TryGetInt(args, "-A", 0));
        var before = Math.Max(ctxC, TryGetInt(args, "-B", 0));

        Regex regex;
        try
        {
            var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (caseInsensitive) opts |= RegexOptions.IgnoreCase;
            if (multiline) opts |= RegexOptions.Singleline; // '.' matches '\n' so a pattern can span lines
            regex = new Regex(pattern, opts);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Error($"Grep: invalid regex: {ex.Message}"));
        }

        IEnumerable<string> files;
        string searchRoot;
        if (Directory.Exists(fullPath))
        {
            searchRoot = fullPath;
            var includes = glob is not null ? [glob]
                : typeAlias is not null && TypeGlobs.TryGetValue(typeAlias, out var tg) ? tg
                : null;

            if (includes is not null)
            {
                var matcher = new Matcher(StringComparison.Ordinal);
                foreach (var inc in includes) matcher.AddInclude(inc);
                var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(fullPath)));
                // Matcher walks the whole tree — drop hits under an ignored dir so bin/obj/node_modules
                // don't sneak back in via a glob/type filter.
                files = result.Files
                    .Where(f => !IsUnderIgnoredDir(f.Path))
                    .Select(f => Path.Combine(fullPath, f.Path));
            }
            else
            {
                files = EnumerateFilesSkippingIgnored(fullPath);
            }
        }
        else if (File.Exists(fullPath))
        {
            searchRoot = Path.GetDirectoryName(fullPath) ?? fullPath;
            files = new[] { fullPath };
        }
        else
        {
            return Task.FromResult(ToolResult.Error($"Grep: path not found: {searchPath}"));
        }

        var perFileCounts = new Dictionary<string, int>();
        var contentBlocks = new List<string>();
        var emittedContentLines = 0;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            string text;
            try
            {
                if (IsProbablyBinary(file)) continue;
                text = File.ReadAllText(file);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var rel = Path.GetRelativePath(searchRoot, file);

            if (multiline)
            {
                var matches = regex.Matches(text);
                if (matches.Count == 0) continue;
                perFileCounts[file] = matches.Count;
                if (outputMode == "content")
                {
                    foreach (Match m in matches)
                    {
                        var startLine = 1 + CountNewlines(text, m.Index);
                        var snippet = m.Value.Replace("\r", "").Replace('\n', '⏎');
                        if (snippet.Length > 200) snippet = snippet[..200] + "…";
                        contentBlocks.Add(lineNumbers ? $"{rel}:{startLine}:{snippet}" : $"{rel}:{snippet}");
                        if (++emittedContentLines >= headLimit && headLimit > 0) break;
                    }
                }
            }
            else
            {
                var lines = SplitLines(text);
                var matchLineIdx = new List<int>();
                for (var i = 0; i < lines.Length; i++)
                    if (regex.IsMatch(lines[i])) matchLineIdx.Add(i);

                if (matchLineIdx.Count == 0) continue;
                perFileCounts[file] = matchLineIdx.Count;

                if (outputMode == "content")
                {
                    var block = FormatContextBlock(rel, lines, matchLineIdx, before, after, lineNumbers);
                    contentBlocks.Add(block);
                    emittedContentLines += block.Count(ch => ch == '\n') + 1;
                }
            }

            if (outputMode == "files_with_matches" && headLimit > 0 && perFileCounts.Count >= headLimit) break;
            if (outputMode == "content" && headLimit > 0 && emittedContentLines >= headLimit) break;
        }

        var output = FormatOutput(outputMode, perFileCounts, contentBlocks, searchRoot);
        return Task.FromResult(ToolResult.Success(output));
    }

    /// <summary>Emit a match's lines plus before/after context, ripgrep-style, merging overlapping
    /// windows and separating disjoint groups with "--".</summary>
    private static string FormatContextBlock(
        string rel, string[] lines, List<int> matchIdx, int before, int after, bool lineNumbers)
    {
        var show = new SortedSet<int>();
        foreach (var m in matchIdx)
            for (var i = Math.Max(0, m - before); i <= Math.Min(lines.Length - 1, m + after); i++)
                show.Add(i);

        var sb = new StringBuilder();
        int? prev = null;
        foreach (var i in show)
        {
            if (prev is int pv && i > pv + 1) sb.AppendLine("--");
            sb.AppendLine(lineNumbers ? $"{rel}:{i + 1}:{lines[i]}" : $"{rel}:{lines[i]}");
            prev = i;
        }
        return sb.ToString().TrimEnd('\n', '\r');
    }

    private static IEnumerable<string> EnumerateFilesSkippingIgnored(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch (IOException) { subDirs = []; }
            catch (UnauthorizedAccessException) { subDirs = []; }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (IgnoredDirs.Contains(name)) continue;
                stack.Push(sub);
            }

            string[] filesInDir;
            try { filesInDir = Directory.GetFiles(dir); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var f in filesInDir) yield return f;
        }
    }

    private static bool IsUnderIgnoredDir(string relativePath)
    {
        foreach (var seg in relativePath.Split('/', '\\'))
            if (IgnoredDirs.Contains(seg)) return true;
        return false;
    }

    /// <summary>Cheap binary sniff: a NUL byte in the first 8 KiB means "not text" — skip it so a
    /// regex doesn't scan (and dump) a compiled artefact that slipped past the dir filter.</summary>
    private static bool IsProbablyBinary(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            Span<byte> buf = stackalloc byte[8192];
            var read = fs.Read(buf);
            for (var i = 0; i < read; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch { return true; } // unreadable → treat as skippable
    }

    private static string[] SplitLines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static int CountNewlines(string text, int uptoIndex)
    {
        var n = 0;
        for (var i = 0; i < uptoIndex && i < text.Length; i++)
            if (text[i] == '\n') n++;
        return n;
    }

    private static string FormatOutput(
        string mode,
        Dictionary<string, int> perFileCounts,
        List<string> contentBlocks,
        string searchRoot)
    {
        if (mode == "count")
        {
            if (perFileCounts.Count == 0) return "(no matches)";
            var sb = new StringBuilder();
            foreach (var kv in perFileCounts)
                sb.AppendLine($"{Path.GetRelativePath(searchRoot, kv.Key)}:{kv.Value}");
            return sb.ToString().TrimEnd();
        }

        if (mode == "content")
        {
            if (contentBlocks.Count == 0) return "(no matches)";
            return string.Join('\n', contentBlocks);
        }

        // default: files_with_matches
        if (perFileCounts.Count == 0) return "(no matches)";
        var sb2 = new StringBuilder();
        foreach (var k in perFileCounts.Keys)
            sb2.AppendLine(Path.GetRelativePath(searchRoot, k));
        return sb2.ToString().TrimEnd();
    }

    private static bool TryGetBool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
            _ => false,
        };
    }

    private static int TryGetInt(JsonElement args, string name, int fallback)
    {
        if (!args.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => fallback,
        };
    }
}
