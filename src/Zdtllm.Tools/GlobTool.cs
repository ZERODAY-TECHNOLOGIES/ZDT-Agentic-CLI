using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Zdtllm.Tools;

public sealed class GlobTool : ITool
{
    private const int MaxResults = 5000;

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

            var matched = result.Files
                .Select(f => Path.Combine(fullPath, f.Path))
                .Where(File.Exists)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(MaxResults)
                .Select(fi => Path.GetRelativePath(fullPath, fi.FullName))
                .ToList();

            if (matched.Count == 0)
                return Task.FromResult(ToolResult.Success("(no matches)"));

            var sb = new StringBuilder();
            sb.AppendLine($"{matched.Count} match(es):");
            foreach (var rel in matched)
                sb.AppendLine(rel);

            return Task.FromResult(ToolResult.Success(sb.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Error($"Glob: failed: {ex.Message}"));
        }
    }
}
