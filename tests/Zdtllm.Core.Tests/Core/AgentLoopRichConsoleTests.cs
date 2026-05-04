using System.Net;
using System.Text;
using Spectre.Console;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// When AgentLoop is constructed with an IAnsiConsole, it suppresses per-delta streaming
/// to the TextWriter and instead renders the final assistant text once, as markdown, to
/// the Spectre console. These tests verify that contract end-to-end with a stub LiteLLM.
/// </summary>
public sealed class AgentLoopRichConsoleTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static (StringWriter sink, IAnsiConsole console) BuildRichConsole()
    {
        var sw = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sw),
            Interactive = InteractionSupport.No,
        });
        console.Profile.Width = 200;
        return (sw, console);
    }

    private static AgentLoop BuildAgent(
        IAnsiConsole? richConsole,
        params HttpResponseMessage[] llmResponses)
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
            richConsole: richConsole);
    }

    [Fact]
    public async Task Rich_console_suppresses_streamed_deltas_on_text_writer_and_renders_markdown_at_end()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"# Hello\\n\\nThis is **bold**.\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var (sink, console) = BuildRichConsole();
        var agent = BuildAgent(console, Sse(sse));

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("hi", output, status);

        // The plain TextWriter ('output') should be empty: the renderer goes through Spectre.
        output.ToString().Should().BeEmpty();

        // The Spectre sink should hold the rendered markdown — readable text without raw markup.
        var rendered = sink.ToString();
        rendered.Should().Contain("Hello");
        rendered.Should().Contain("bold");
        rendered.Should().NotContain("**bold**");
        rendered.Should().NotContain("# Hello");

        result.FinalText.Should().Contain("# Hello");
        result.FinalText.Should().Contain("**bold**");
    }

    [Fact]
    public async Task Plain_console_keeps_streaming_deltas_to_text_writer()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"plain words\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var agent = BuildAgent(richConsole: null, Sse(sse));

        var output = new StringWriter();
        var status = new StringWriter();
        await agent.RunOneShotAsync("hi", output, status);

        // Plain mode keeps the existing contract: text deltas go to the TextWriter.
        output.ToString().Should().Contain("plain words");
    }
}
