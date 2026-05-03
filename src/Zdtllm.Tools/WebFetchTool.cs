using System.Net.Http;
using System.Text;
using System.Text.Json;

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
        Description: "Fetch a URL over HTTP(S) and return its text body. Body is truncated at 256 KiB.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "Absolute URL to fetch." },
                prompt = new { type = "string", description = "Optional hint about what to extract (informational only — the raw body is returned)." },
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
