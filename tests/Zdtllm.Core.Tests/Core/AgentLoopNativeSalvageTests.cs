using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// Native-mode salvage: on a raw-passthrough LiteLLM route with no server-side GLM tool parser, GLM
/// emits &lt;function_calls&gt;/&lt;tool_call&gt; markup in delta.content instead of JSON tool_calls.
/// Before this the markup was rendered as the final answer while the task stalled silently; now the
/// loop parses and dispatches the calls (via the XML round) and warns once.
/// </summary>
public sealed class AgentLoopNativeSalvageTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static string ContentRound(string content) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content } } } }) + "\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private static AgentLoop BuildAgent(StubHandler handler, ToolRegistry registry)
    {
        var client = new LiteLLMClient(new HttpClient(handler), new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "glm-5.2:cloud", MaxTurns = 5, ToolCallingMode = ToolCallingMode.Native });
    }

    [Fact]
    public async Task Native_mode_salvages_tool_call_markup_emitted_in_content()
    {
        const string markup =
            "<function_calls><invoke name=\"Peek\"><parameter name=\"q\">x</parameter></invoke></function_calls>";
        var handler = new StubHandler(Sse(ContentRound(markup)), Sse(ContentRound("all done")));
        var registry = new ToolRegistry();
        var peek = new CountingTool("Peek");
        registry.Register(peek);

        var agent = BuildAgent(handler, registry);
        var result = await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        peek.Invocations.Should().Be(1);            // salvaged from content and dispatched
        result.FinalText.Should().Contain("all done");
    }

    [Fact]
    public async Task Plain_content_is_not_treated_as_a_tool_call()
    {
        var handler = new StubHandler(Sse(ContentRound("just a normal answer, no markup")));
        var registry = new ToolRegistry();
        var peek = new CountingTool("Peek");
        registry.Register(peek);

        var agent = BuildAgent(handler, registry);
        var result = await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        peek.Invocations.Should().Be(0);
        result.FinalText.Should().Contain("just a normal answer");
    }

    private sealed class CountingTool : ITool
    {
        private readonly string _name;
        public int Invocations { get; private set; }
        public CountingTool(string name) => _name = name;

        public ToolSchema Schema => new(_name, $"{_name} tool.",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(ToolResult.Success("peeked"));
        }
    }
}
