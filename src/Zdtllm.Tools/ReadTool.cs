using System.Text;
using System.Text.Json;

namespace Zdtllm.Tools;

public sealed class ReadTool : ITool
{
    private const int DefaultLimit = 2000;

    public ToolSchema Schema { get; } = new(
        Name: "Read",
        Description: "Read a file from the local filesystem and return its contents with line numbers (1-indexed). Supports an optional offset and limit (in lines).",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Absolute or relative path to the file." },
                offset = new { type = "integer", description = "Line number to start reading from (1-indexed)." },
                limit = new { type = "integer", description = "Maximum number of lines to read." },
            },
            required = new[] { "path" },
        }));

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            return ToolResult.Error("Read: missing or invalid 'path' parameter.");

        var path = pathProp.GetString()!;
        var fullPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ctx.Cwd, path));

        if (!File.Exists(fullPath))
            return ToolResult.Error($"Read: file not found: {path}");

        var offset = TryGetInt(args, "offset", 1);
        var limit = TryGetInt(args, "limit", DefaultLimit);

        if (offset < 1) offset = 1;
        if (limit < 1) limit = DefaultLimit;

        try
        {
            var lines = await File.ReadAllLinesAsync(fullPath, ct);
            var slice = lines.Skip(offset - 1).Take(limit).ToArray();

            var sb = new StringBuilder(capacity: slice.Length * 80);
            for (var i = 0; i < slice.Length; i++)
            {
                sb.Append((offset + i).ToString().PadLeft(6));
                sb.Append('\t');
                sb.AppendLine(slice[i]);
            }

            if (offset > 1 || lines.Length > offset - 1 + slice.Length)
                sb.AppendLine($"\n[showing lines {offset}-{offset + slice.Length - 1} of {lines.Length}]");

            return ToolResult.Success(sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Read: failed to read '{path}': {ex.Message}");
        }
    }

    private static int TryGetInt(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : fallback;
}
