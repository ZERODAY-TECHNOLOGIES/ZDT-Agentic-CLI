using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zdtllm.Tools;

public sealed class WebFetchTool : ITool
{
    private const int MaxBodyBytes = 256 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    public WebFetchTool(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    public ToolSchema Schema { get; } = new(
        Name: "WebFetch",
        Description: "Fetch a URL over HTTP(S) and return its text body. HTML is reduced to readable text (scripts/styles/markup stripped). Body is truncated at 256 KiB.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "Absolute URL to fetch." },
                prompt = new { type = "string", description = "Optional hint about what to extract (informational only — the readable body is returned)." },
                raw = new { type = "boolean", description = "Return the raw response body without HTML→text conversion (default false)." },
            },
            required = new[] { "url" },
        }));

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String)
            return ToolResult.Error("WebFetch: missing or invalid 'url' parameter.");

        var urlString = u.GetString()!;
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ToolResult.Error($"WebFetch: '{urlString}' is not an absolute http(s) URL.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = await ReadBodyAsync(response, cts.Token).ConfigureAwait(false);

            // When the response is HTML (and the caller didn't ask for raw), reduce it to readable
            // text so the model isn't handed tag soup, inline scripts, and CSS. Detected by
            // Content-Type or a leading <!doctype/<html sniff (some servers mislabel).
            var raw = TryGetBool(args, "raw");
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var looksHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                || LooksLikeHtml(body);
            if (!raw && looksHtml)
                body = HtmlToText(body);

            var sb = new StringBuilder();
            sb.AppendLine($"GET {urlString} → HTTP {status} {response.StatusCode}");
            if (response.Content.Headers.ContentType is { } ct2)
                sb.AppendLine($"Content-Type: {ct2}");
            sb.AppendLine();
            sb.Append(body);

            return response.IsSuccessStatusCode
                ? ToolResult.Success(sb.ToString())
                : ToolResult.Error(sb.ToString());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Error($"WebFetch: request to {urlString} timed out after {DefaultTimeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"WebFetch: {ex.Message}");
        }
    }

    private static bool LooksLikeHtml(string body)
    {
        var head = body.AsSpan(0, Math.Min(body.Length, 512)).ToString().TrimStart();
        return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex ScriptStyle = new(
        @"<(script|style|head|noscript|svg)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BlockBreak = new(
        @"</(p|div|li|ul|ol|tr|h[1-6]|section|article|header|footer|blockquote)>|<br\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyTag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ManyBlankLines = new(@"\n[ \t]*\n[ \t]*(\n[ \t]*)+", RegexOptions.Compiled);

    /// <summary>Dependency-free HTML→text: drop script/style/head, turn block-closers into newlines,
    /// strip remaining tags, decode entities, and collapse runaway blank lines. Good enough to give
    /// the model the readable content without a heavy HTML parser.</summary>
    internal static string HtmlToText(string html)
    {
        var s = ScriptStyle.Replace(html, "\n");
        s = BlockBreak.Replace(s, "\n");
        s = AnyTag.Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        // Normalise whitespace: trim each line, collapse 3+ blank lines to one.
        var lines = s.Split('\n').Select(l => l.Trim());
        s = string.Join('\n', lines);
        s = ManyBlankLines.Replace(s, "\n\n");
        return s.Trim();
    }

    private static bool TryGetBool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v)) return false;
        return v.ValueKind is JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBodyBytes];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }
        var text = Encoding.UTF8.GetString(buffer, 0, totalRead);

        // Was the response longer than our cap?
        var probe = new byte[1];
        if (await stream.ReadAsync(probe, ct).ConfigureAwait(false) > 0)
            text += "\n[body truncated at 256 KiB]";
        return text;
    }
}
