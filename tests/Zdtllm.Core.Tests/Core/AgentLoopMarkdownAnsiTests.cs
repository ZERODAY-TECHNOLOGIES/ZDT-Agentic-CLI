using System.Net;
using System.Text;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// The bottom-input TUI can't host Spectre renderables (its scroll region takes plain ANSI text
/// lines), so AgentLoop accepts a markdown→ANSI-string renderer instead of a rich console: deltas
/// are buffered and the final text is written to the plain output writer as one rendered block —
/// never as raw ###/**/` markdown noise.
/// </summary>
public sealed class AgentLoopMarkdownAnsiTests
{
    private static readonly string Esc = ((char)0x1B).ToString();

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static AgentLoop BuildAgent(params HttpResponseMessage[] llmResponses)
    {
        var handler = new StubHandler(llmResponses);
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(
            client,
            new ToolRegistry(),
            PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 },
            markdownAnsi: md => MarkdownRenderer.RenderToAnsi(md, 100));
    }

    [Fact]
    public async Task Markdown_ansi_path_renders_final_text_instead_of_streaming_raw_markdown()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"### Hello\\n\\nThis is **bold**.\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var agent = BuildAgent(Sse(sse));

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("hi", output, status);

        var written = output.ToString();
        // Rendered, not raw: the text is present but the markdown markers are consumed.
        written.Should().Contain("Hello");
        written.Should().Contain("bold");
        written.Should().NotContain("### Hello");
        written.Should().NotContain("**bold**");
        // It IS an ANSI render (colors present — ESC '[' introducer in the output).
        written.Should().Contain(Esc + "[");

        // Session/result text keeps the raw markdown (history fidelity).
        result.FinalText.Should().Contain("### Hello");
        result.FinalText.Should().Contain("**bold**");
    }

    [Fact]
    public void RenderToAnsi_preserves_blank_separators_as_single_spaces()
    {
        var ansi = MarkdownRenderer.RenderToAnsi("para one\n\npara two", 60);
        // Blank separator lines become " " so line-buffered sinks that drop empty
        // lines keep the vertical rhythm.
        var lines = ansi.Replace("\r\n", "\n").Split('\n');
        lines.Should().Contain(" ");
        ansi.Should().Contain("para one");
        ansi.Should().Contain("para two");
    }
}
