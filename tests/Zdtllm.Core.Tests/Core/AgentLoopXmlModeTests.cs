using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

public sealed class AgentLoopXmlModeTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static AgentLoop BuildAgent(StubHandler handler, ToolRegistry registry, ToolCallingMode mode)
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(
            client,
            registry,
            PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "qwen-test", MaxTurns = 5, ToolCallingMode = mode });
    }

    /// <summary>
    /// Encode literal '<' and '>' inside an SSE JSON payload. We can't put them
    /// raw because System.Text.Json escapes them by default, and our parser
    /// tolerates the < escape just fine, but readable assertions need the
    /// real characters. The cleanest path is to build the JSON via the serializer
    /// itself and accept its default escaping.
    /// </summary>
    private static string SseTextChunk(string text)
    {
        var payload = new
        {
            choices = new[]
            {
                new { delta = new { content = text } },
            },
        };
        return "data: " + JsonSerializer.Serialize(payload) + "\n\n";
    }

    private static string SseFinish(string reason) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = reason } },
        }) + "\n\n";

    private const string SseDone = "data: [DONE]\n\n";

    [Fact]
    public async Task Xml_mode_extracts_call_executes_tool_and_continues()
    {
        // Round 1: model emits a <function_calls> block in plain text.
        var round1 =
            SseTextChunk("I'll read it.\n<function_calls>\n<invoke name=\"Echo\">\n<parameter name=\"text\">x</parameter>\n</invoke>\n</function_calls>") +
            SseFinish("stop") + SseDone;

        // Round 2: model emits the final answer (no XML).
        var round2 = SseTextChunk("done") + SseFinish("stop") + SseDone;

        var handler = new StubHandler(Sse(round1), Sse(round2));

        var registry = new ToolRegistry();
        var echo = new EchoTool();
        registry.Register(echo);

        var agent = BuildAgent(handler, registry, ToolCallingMode.Xml);

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("please echo", output, status);

        result.FinalText.Should().Contain("done");
        result.Turns.Should().Be(2);
        echo.Invocations.Should().Be(1);
        echo.LastTextArg.Should().Be("x");

        // Round 1 must NOT include a native `tools` array (XML mode skips it).
        handler.RequestBodies[0].Should().NotContain("\"tools\":");

        // Round 2 conversation history: tool result is sent back as a USER turn
        // formatted as EXECUTION RESULT, not as a `tool` role message.
        var secondBody = handler.RequestBodies[1];
        secondBody.Should().Contain("EXECUTION RESULT of [Echo]");
        secondBody.Should().Contain("echoed:x");
    }

    [Fact]
    public async Task Xml_mode_includes_tool_catalog_in_system_prompt()
    {
        var round1 = SseTextChunk("hello") + SseFinish("stop") + SseDone;
        var handler = new StubHandler(Sse(round1));

        var registry = new ToolRegistry();
        registry.Register(new EchoTool());

        var agent = BuildAgent(handler, registry, ToolCallingMode.Xml);

        await agent.RunOneShotAsync("hi", new StringWriter(), new StringWriter());

        var body = handler.RequestBodies[0];
        body.Should().Contain("Tool calling protocol");
        body.Should().Contain("Echo");
        // Native tools array must NOT be sent in XML mode.
        body.Should().NotContain("\"tools\":");
    }

    [Fact]
    public async Task Xml_mode_returns_clean_text_with_function_calls_stripped()
    {
        // Single round, no tool call, just plain text with a stray (orphan) block — verify
        // that whatever XML appears is stripped from the user-visible final text.
        var content = "Here is your answer.\n<function_calls></function_calls>\nThe end.";
        var round1 = SseTextChunk(content) + SseFinish("stop") + SseDone;

        var handler = new StubHandler(Sse(round1));
        var agent = BuildAgent(handler, new ToolRegistry(), ToolCallingMode.Xml);

        var output = new StringWriter();
        var result = await agent.RunOneShotAsync("hi", output, new StringWriter());

        result.FinalText.Should().Contain("Here is your answer.");
        result.FinalText.Should().Contain("The end.");
        result.FinalText.Should().NotContain("function_calls");
        output.ToString().Should().NotContain("function_calls");
    }

    [Fact]
    public async Task Xml_mode_collapses_multiple_tool_results_into_single_user_turn()
    {
        // Regression test for the v0.2.x bug where N tool calls in one assistant turn
        // produced N CONSECUTIVE user messages — Qwen3-Coder's chat template (and vLLM's
        // OpenAI compat layer) reject that with the misleading error "System message must
        // be at the beginning". Confirmed live against vLLM running Qwen during a Siembiot
        // SAST scan: Qwen issued 10 Read calls in a single turn, the next request 400ed.
        // After the fix all results are framed as a single EXECUTION RESULT-delimited
        // user turn, preserving strict user/assistant alternation.
        var round1 =
            SseTextChunk("Reading both files.\n" +
                "<function_calls>\n" +
                "<invoke name=\"Echo\">\n<parameter name=\"text\">first</parameter>\n</invoke>\n" +
                "<invoke name=\"Echo\">\n<parameter name=\"text\">second</parameter>\n</invoke>\n" +
                "<invoke name=\"Echo\">\n<parameter name=\"text\">third</parameter>\n</invoke>\n" +
                "</function_calls>") +
            SseFinish("stop") + SseDone;
        var round2 = SseTextChunk("done") + SseFinish("stop") + SseDone;

        var handler = new StubHandler(Sse(round1), Sse(round2));
        var registry = new ToolRegistry();
        registry.Register(new EchoTool());
        var agent = BuildAgent(handler, registry, ToolCallingMode.Xml);

        await agent.RunOneShotAsync("echo three", new StringWriter(), new StringWriter());

        var secondBody = handler.RequestBodies[1];

        // All three results must appear, but inside a SINGLE user message — count the
        // closing role boundary ("role":"user") instead of the EXECUTION RESULT marker
        // because we want to assert there's only one synthetic-result message, not three.
        // Round 2 messages: [system, original-user, assistant, this-one-user] = 2 user
        // role entries total in the JSON. Anything more = bug back.
        var userRoleCount = System.Text.RegularExpressions.Regex.Matches(secondBody, "\"role\":\"user\"").Count;
        userRoleCount.Should().Be(2);

        secondBody.Should().Contain("EXECUTION RESULT of [Echo]");
        secondBody.Should().Contain("echoed:first");
        secondBody.Should().Contain("echoed:second");
        secondBody.Should().Contain("echoed:third");
    }

    [Fact]
    public async Task Native_mode_still_works_unchanged()
    {
        var round1 =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0,
                                    id = "c1",
                                    type = "function",
                                    function = new { name = "Echo", arguments = "{\"text\":\"y\"}" },
                                },
                            },
                        },
                    },
                },
            }) + "\n\n" +
            SseFinish("tool_calls") + SseDone;

        var round2 = SseTextChunk("ok") + SseFinish("stop") + SseDone;
        var handler = new StubHandler(Sse(round1), Sse(round2));

        var registry = new ToolRegistry();
        var echo = new EchoTool();
        registry.Register(echo);

        var agent = BuildAgent(handler, registry, ToolCallingMode.Native);

        var result = await agent.RunOneShotAsync("hi", new StringWriter(), new StringWriter());

        echo.Invocations.Should().Be(1);
        echo.LastTextArg.Should().Be("y");
        result.FinalText.Should().Be("ok");

        // Native mode DOES send the tools array.
        handler.RequestBodies[0].Should().Contain("\"tools\":");
    }

    private sealed class EchoTool : ITool
    {
        public int Invocations { get; private set; }
        public string? LastTextArg { get; private set; }

        public ToolSchema Schema { get; } = new(
            "Echo",
            "Echo back the text argument prefixed with 'echoed:'.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    text = new { type = "string", description = "Text to echo." },
                },
                required = new[] { "text" },
            }));

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Invocations++;
            LastTextArg = args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            return Task.FromResult(ToolResult.Success($"echoed:{LastTextArg}"));
        }
    }
}
