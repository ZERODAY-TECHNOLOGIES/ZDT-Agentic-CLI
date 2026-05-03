using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Zdtllm.Tools;

public sealed class GrepTool : ITool
{
    private const int DefaultHeadLimit = 250;

    public ToolSchema Schema { get; } = new(
        Name: "Grep",
        Description: "Search file contents for a regex. Default output is the list of matching files (\"files_with_matches\"); set output_mode to \"content\" to see matching lines or \"count\" to see per-file match counts. Pattern is .NET regex syntax.",
        Parameters: JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["pattern"] = new { type = "string", description = ".NET regex pattern." },
                ["path"] = new { type = "string", description = "Directory or file to search (default: current working directory)." },
                ["glob"] = new { type = "string", description = "Optional file glob (e.g. \"*.cs\") that restricts which files are scanned." },
                ["output_mode"] = new { type = "string", description = "files_with_matches (default) | content | count." },
                ["head_limit"] = new { type = "integer", description = "Max number of result lines (default 250)." },
                ["-i"] = new { type = "boolean", description = "Case-insensitive match." },
                ["-n"] = new { type = "boolean", description = "In content mode, prefix each line with its 1-based line number." },
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
        var outputMode = (args.TryGetProperty("output_mode", out var om) && om.ValueKind == JsonValueKind.String
            ? om.GetString()!
            : "files_with_matches").ToLowerInvariant();
        var caseInsensitive = TryGetBool(args, "-i");
        var lineNumbers = TryGetBool(args, "-n");
        var headLimit = TryGetInt(args, "head_limit", DefaultHeadLimit);

        Regex regex;
        try
        {
            var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (caseInsensitive) opts |= RegexOptions.IgnoreCase;
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
            if (glob is not null)
            {
                var matcher = new Matcher(StringComparison.Ordinal);
                matcher.AddInclude(glob);
                var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(fullPath)));
                files = result.Files.Select(f => Path.Combine(fullPath, f.Path));
            }
            else
            {
                files = Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories);
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
        var contentLines = new List<string>();

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            int fileMatches = 0;
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;

                fileMatches++;
                if (outputMode == "content")
                {
                    var rel = Path.GetRelativePath(searchRoot, file);
                    contentLines.Add(lineNumbers
                        ? $"{rel}:{i + 1}:{lines[i]}"
                        : $"{rel}:{lines[i]}");
                    if (headLimit > 0 && contentLines.Count >= headLimit) break;
                }
            }

            if (fileMatches > 0) perFileCounts[file] = fileMatches;

            if (outputMode == "files_with_matches" && headLimit > 0 && perFileCounts.Count >= headLimit) break;
            if (outputMode == "content" && headLimit > 0 && contentLines.Count >= headLimit) break;
        }

        var output = FormatOutput(outputMode, perFileCounts, contentLines, searchRoot);
        return Task.FromResult(ToolResult.Success(output));
    }

    private static string FormatOutput(
        string mode,
        Dictionary<string, int> perFileCounts,
        List<string> contentLines,
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
            if (contentLines.Count == 0) return "(no matches)";
            return string.Join('\n', contentLines);
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
