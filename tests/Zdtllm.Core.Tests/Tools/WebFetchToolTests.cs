using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class WebFetchToolTests
{
    private static HttpResponseMessage Ok(string body, string contentType = "text/plain") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static async Task<ToolResult> FetchAsync(StubHandler handler, string url)
    {
        var http = new HttpClient(handler);
        var tool = new WebFetchTool(http);
        var argsJson = JsonSerializer.Serialize(new { url });
        using var doc = JsonDocument.Parse(argsJson);
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);
    }

    [Fact]
    public async Task Returns_body_on_2xx_response()
    {
        var handler = new StubHandler(Ok("hello world"));

        var result = await FetchAsync(handler, "https://example.com/x");

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("HTTP 200");
        result.Content.Should().Contain("Content-Type: text/plain");
        result.Content.Should().Contain("hello world");
    }

    [Fact]
    public async Task Returns_error_on_non_2xx()
    {
        var handler = new StubHandler(Status(HttpStatusCode.NotFound, "not here"));

        var result = await FetchAsync(handler, "https://example.com/missing");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("HTTP 404");
    }

    [Fact]
    public async Task Rejects_non_http_urls()
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler);
        var tool = new WebFetchTool(http);
        var argsJson = JsonSerializer.Serialize(new { url = "ftp://example.com/x" });
        using var doc = JsonDocument.Parse(argsJson);

        var result = await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("absolute http(s)");
    }

    [Fact]
    public void Specifier_for_permissions_is_the_url()
    {
        var http = new HttpClient(new StubHandler());
        var tool = new WebFetchTool(http);
        using var doc = JsonDocument.Parse("""{"url":"https://example.com/x"}""");

        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("https://example.com/x");
    }
}
