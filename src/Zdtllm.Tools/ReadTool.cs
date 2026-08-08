using System.Text;
using System.Text.Json;

namespace Zdtllm.Tools;

public sealed class ReadTool : ITool
{
    private const int DefaultLimit = 2000;

    /// <summary>
    /// Per-call output budget (characters). Read paginates by LINE (offset/limit), but a file can hold a
    /// huge amount on few lines — e.g. a 1.5 MB minified/JSON data blob on ONE line — and line limits
    /// don't bound that. Without a byte budget a single Read could dump ~975k tokens and blow the model's
    /// context window. So we cap each call at ~this many chars and PAGINATE: for multi-line files, stop at
    /// a line boundary and tell the model the next offset; for a single over-budget line, show the head
    /// and point at Grep / Bash byte-slicing for the rest. ~100k chars ≈ 25k tokens — generous for real
    /// source, small enough that ten reads don't exhaust the window.
    /// </summary>
    private const int MaxCharsPerRead = 100_000;

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
        Description: "Read a file from the local filesystem and return its contents with line numbers (1-indexed). Supports an optional offset and limit (in lines). Output is capped per call (~100KB) and paginated: when a file is large the result ends with the next offset to continue from.",
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
            var total = lines.Length;
            if (total == 0) return ToolResult.Success("(empty file)");

            var startIdx = offset - 1; // 0-based
            if (startIdx >= total)
                return ToolResult.Success($"[offset {offset} is past the end of the file ({total} lines)]");

            var sb = new StringBuilder(capacity: Math.Min(MaxCharsPerRead, 64 * 1024));
            var emitted = 0;         // lines emitted this call
            var chars = 0;           // chars written so far (budget)
            var budgetHit = false;   // stopped because the char budget was reached
            var partialLine = false; // a single line exceeded the whole budget; we showed its head only

            var i = startIdx;
            for (; i < total && emitted < limit; i++)
            {
                var prefix = (i + 1).ToString().PadLeft(6);
                var line = lines[i];
                var cost = prefix.Length + 1 /*tab*/ + line.Length + 1 /*newline*/;

                if (chars == 0 && cost > MaxCharsPerRead)
                {
                    // One line bigger than the entire budget — show a leading slice, then stop.
                    var room = Math.Max(0, MaxCharsPerRead - prefix.Length - 1);
                    sb.Append(prefix).Append('\t').Append(line.AsSpan(0, Math.Min(room, line.Length))).Append('\n');
                    emitted++;
                    partialLine = true;
                    budgetHit = true;
                    break;
                }
                if (chars > 0 && chars + cost > MaxCharsPerRead)
                {
                    budgetHit = true;
                    break;
                }

                sb.Append(prefix).Append('\t').Append(line).Append('\n');
                chars += cost;
                emitted++;
            }

            var lastShown = offset + emitted - 1;

            if (partialLine)
            {
                var lineLen = lines[startIdx].Length;
                sb.Append($"\n[line {offset} is very large ({lineLen:N0} chars) — showed the first ~{MaxCharsPerRead / 1000}K only. " +
                    "Looks like a single-line data blob (minified/JSON). Read specific parts with Grep for a key, or " +
                    $"slice bytes with Bash (e.g. `cut -c {MaxCharsPerRead + 1}-{MaxCharsPerRead * 2} <file>`) — reading it whole wastes context.]");
            }
            else if (budgetHit)
            {
                sb.Append($"\n[showing lines {offset}-{lastShown} of {total} — output capped at ~{MaxCharsPerRead / 1000}K. " +
                    $"Continue with offset: {lastShown + 1}]");
            }
            else if (offset > 1 || total > lastShown)
            {
                sb.Append($"\n[showing lines {offset}-{lastShown} of {total}]");
            }

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
