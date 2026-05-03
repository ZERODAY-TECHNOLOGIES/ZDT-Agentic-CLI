using System.Text.Json;

namespace Zdtllm.Tools;

public sealed class EditTool : ITool
{
    public ToolSchema Schema { get; } = new(
        Name: "Edit",
        Description: "Replace text in a file by exact-match. By default the old_string must occur exactly once in the file (so the edit is unambiguous); pass replace_all=true to replace every occurrence.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                file_path = new { type = "string", description = "Absolute or relative path to the file." },
                old_string = new { type = "string", description = "Text to find. Must match exactly (whitespace included)." },
                new_string = new { type = "string", description = "Replacement text." },
                replace_all = new { type = "boolean", description = "If true, replace every occurrence (default false: require unique match)." },
            },
            required = new[] { "file_path", "old_string", "new_string" },
        }));

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("file_path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("file_path", out var fp) || fp.ValueKind != JsonValueKind.String)
            return ToolResult.Error("Edit: missing or invalid 'file_path' parameter.");
        if (!args.TryGetProperty("old_string", out var os) || os.ValueKind != JsonValueKind.String)
            return ToolResult.Error("Edit: missing or invalid 'old_string' parameter.");
        if (!args.TryGetProperty("new_string", out var ns) || ns.ValueKind != JsonValueKind.String)
            return ToolResult.Error("Edit: missing or invalid 'new_string' parameter.");

        var path = fp.GetString()!;
        var oldStr = os.GetString()!;
        var newStr = ns.GetString()!;
        var replaceAll = args.TryGetProperty("replace_all", out var ra) &&
                         (ra.ValueKind == JsonValueKind.True ||
                          (ra.ValueKind == JsonValueKind.String && bool.TryParse(ra.GetString(), out var b) && b));

        if (oldStr.Length == 0)
            return ToolResult.Error("Edit: old_string is empty; nothing to find.");
        if (oldStr == newStr)
            return ToolResult.Error("Edit: old_string and new_string are identical; no change to make.");

        var fullPath = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ctx.Cwd, path));
        if (!File.Exists(fullPath))
            return ToolResult.Error($"Edit: file not found: {path}");

        try
        {
            var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);

            var count = CountOccurrences(content, oldStr);
            if (count == 0)
                return ToolResult.Error($"Edit: old_string not found in {path}.");
            if (count > 1 && !replaceAll)
                return ToolResult.Error(
                    $"Edit: old_string occurs {count} times in {path} but replace_all is false. " +
                    "Use replace_all=true or provide a more specific old_string.");

            string newContent;
            int replaced;
            if (replaceAll)
            {
                newContent = content.Replace(oldStr, newStr, StringComparison.Ordinal);
                replaced = count;
            }
            else
            {
                var idx = content.IndexOf(oldStr, StringComparison.Ordinal);
                newContent = string.Concat(content.AsSpan(0, idx), newStr, content.AsSpan(idx + oldStr.Length));
                replaced = 1;
            }

            await File.WriteAllTextAsync(fullPath, newContent, ct).ConfigureAwait(false);

            return ToolResult.Success($"Edited {path}: replaced {replaced} occurrence(s).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"Edit: failed: {ex.Message}");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
