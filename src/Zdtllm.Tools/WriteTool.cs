using System.Text.Json;

namespace Zdtllm.Tools;

public sealed class WriteTool : ITool
{
    public ToolSchema Schema { get; } = new(
        Name: "Write",
        Description: "Write text content to a file, overwriting any existing file. Creates parent directories as needed. Use Edit for partial modifications.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                file_path = new { type = "string", description = "Absolute or relative path to the file to write." },
                content = new { type = "string", description = "Content to write. UTF-8." },
            },
            required = new[] { "file_path", "content" },
        }));

    /// <summary>Two concurrent Writes to the same path race — keep them serial.</summary>
    public bool CanRunInParallel => false;

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("file_path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("file_path", out var fp) || fp.ValueKind != JsonValueKind.String)
            return ToolResult.Error("Write: missing or invalid 'file_path' parameter.");
        if (!args.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.String)
            return ToolResult.Error("Write: missing or invalid 'content' parameter.");

        var path = fp.GetString()!;
        var content = contentProp.GetString() ?? string.Empty;
        var fullPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ctx.Cwd, path));

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);

            var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            return ToolResult.Success($"Wrote {bytes} bytes to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Write: failed to write '{path}': {ex.Message}");
        }
    }
}
