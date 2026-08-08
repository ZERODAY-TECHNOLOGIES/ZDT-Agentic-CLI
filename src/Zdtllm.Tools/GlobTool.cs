using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Zdtllm.Tools;

public sealed class GlobTool : ITool
{
    // Lower than the old 5000: a glob returning thousands of paths is unusable and eats the context
    // window. Combined with SearchExclusions (which prunes .git/bin/obj/node_modules AND zdt's own
    // .zdtllm session dir), a real pattern returns a manageable list; over-broad ones get truncated
    // with a "narrow it" hint rather than dumping the whole tree.
    private const int MaxResults = 1000;

    public ToolSchema Schema { get; } = new(
        Name: "Glob",
        Description: "Find files whose path matches a glob pattern. Supports `*` (any chars within a path segment), `**` (recursive), `?` (single char). Results are sorted by last-modified time, newest first.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pattern = new { type = "string", description = "Glob pattern, e.g. \"**/*.cs\" or \"src/*.txt\"." },
                path = new { type = "string", description = "Directory to search in (default: current working directory)." },
            },
            required = new[] { "pattern" },
        }));

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("pattern", out var p) || p.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("Glob: missing or invalid 'pattern' parameter."));

        var pattern = p.GetString()!;
        var searchPath = args.TryGetProperty("path", out var sp) && sp.ValueKind == JsonValueKind.String
            ? sp.GetString()!
            : ctx.Cwd;
        var fullPath = Path.IsPathRooted(searchPath)
            ? searchPath
            : Path.GetFullPath(Path.Combine(ctx.Cwd, searchPath));

        if (!Directory.Exists(fullPath))
            return Task.FromResult(ToolResult.Error($"Glob: directory not found: {searchPath}"));

        try
        {
            var matcher = new Matcher(StringComparison.Ordinal);
            matcher.AddInclude(pattern);
            var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(fullPath)));

            var all = result.Files
                .Where(f => !SearchExclusions.IsUnderIgnoredDir(f.Path)) // skip .git/bin/obj/.zdtllm/…
                .Select(f => Path.Combine(fullPath, f.Path))
                .Where(File.Exists)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();

            if (all.Count == 0)
                return Task.FromResult(ToolResult.Success("(no matches)"));

            var truncated = all.Count > MaxResults;
            var shown = all.Take(MaxResults)
                .Select(fi => Path.GetRelativePath(fullPath, fi.FullName))
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine(truncated
                ? $"{shown.Count} of {all.Count} match(es) (truncated — narrow the pattern or search a subdirectory):"
                : $"{shown.Count} match(es):");
            foreach (var rel in shown)
                sb.AppendLine(rel);

            return Task.FromResult(ToolResult.Success(sb.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Error($"Glob: failed: {ex.Message}"));
        }
    }
}
