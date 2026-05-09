using System.Text;
using System.Text.Json;

namespace Zdtllm.Tools;

public sealed class ReadTool : ITool
{
    private const int DefaultLimit = 2000;

    /// <summary>
    /// Hard cap on file size before <see cref="ExecuteAsync"/> bails out. Defense-in-depth
    /// against the agent reading multi-MB / multi-GB blobs (assets, dumps, parquet, wasm) —
    /// <see cref="File.ReadAllLinesAsync(string, CancellationToken)"/> materialises the
    /// whole file as a <c>string[]</c>, so a 200 MB pg_dump.sql would OOM the process and
    /// burn the entire context window in one tool call. Permission rules remain the
    /// primary line of defense; this cap catches the case where perms are open
    /// (--dangerously-skip-permissions, or a path the user didn't anticipate denying).
    ///
    /// 5 MiB is generous enough that real source files (including auto-generated
    /// TypeScript / SQL migrations / large JSON fixtures) pass through, while still
    /// stopping the worst offenders. Above this, the user is told what happened and
    /// pointed at Glob with extension filters as the proper way to find text sources.
    /// Binary detection is intentionally NOT done — NUL-byte heuristics false-positive on
    /// UTF-16-LE files (PowerShell scripts, .resx) and the cost of letting an occasional
    /// small binary through is bounded by the cap above.
    /// </summary>
    private const long MaxBytesForRead = 5L * 1024 * 1024;

    public ToolSchema Schema { get; } = new(
        Name: "Read",
        Description: "Read a file from the local filesystem and return its contents with line numbers (1-indexed). Supports an optional offset and limit (in lines).",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                file_path = new { type = "string", description = "Absolute or relative path to the file." },
                offset = new { type = "integer", description = "Line number to start reading from (1-indexed)." },
                limit = new { type = "integer", description = "Maximum number of lines to read." },
            },
            required = new[] { "file_path" },
        }));

    public string? GetSpecifierForPermissions(JsonElement args) =>
        TryGetPath(args);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = TryGetPath(args);
        if (string.IsNullOrEmpty(path))
            return ToolResult.Error("Read: missing or invalid 'file_path' parameter.");

        var fullPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ctx.Cwd, path));

        if (!File.Exists(fullPath))
            return ToolResult.Error($"Read: file not found: {path}");

        // Size cap fires before we hit ReadAllLinesAsync — see MaxBytesForRead doc-comment
        // for the why. FileInfo.Length is a metadata read, not a stream open, so it's cheap.
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxBytesForRead)
            return ToolResult.Error(
                $"Read: file too large ({fileInfo.Length / 1024} KiB > {MaxBytesForRead / 1024} KiB cap). " +
                "Likely an asset, binary, or dump file. Use Glob with extension filters " +
                "(e.g. **/*.cs, **/*.ts) to target text source files instead.");

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

    /// <summary>
    /// Accept both <c>file_path</c> (claude-cli canonical, matches Write/Edit) and the legacy
    /// <c>path</c> name. Native-mode tool calls follow the schema and use <c>file_path</c>;
    /// xml-mode prompts and older sessions still emit <c>path</c>, so we keep it as an alias
    /// instead of breaking on a mid-conversation /tool-calling switch.
    /// </summary>
    private static string? TryGetPath(JsonElement args)
    {
        if (args.TryGetProperty("file_path", out var fp) && fp.ValueKind == JsonValueKind.String)
            return fp.GetString();
        if (args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString();
        return null;
    }

    private static int TryGetInt(JsonElement args, string name, int fallback)
    {
        if (!args.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            // XML-mode tool calls deliver integers as strings; coerce gracefully.
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => fallback,
        };
    }
}
