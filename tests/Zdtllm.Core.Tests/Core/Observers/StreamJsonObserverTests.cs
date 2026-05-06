using System.Collections.Immutable;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Observers;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.Core.Observers;

/// <summary>
/// StreamJsonObserver emits Anthropic-compatible NDJSON: one event per assistant iteration
/// (carrying text + tool_use blocks + per-turn usage) and exactly one terminal "result"
/// event. The shape matches what AppSec-Automator's StreamJsonResult.php parses, so these
/// tests assert on the exact field names and nesting that downstream consumer relies on.
/// </summary>
public sealed class StreamJsonObserverTests
{
    private static IReadOnlyList<JsonElement> ParseNdjson(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    [Fact]
    public async Task Assistant_event_has_message_with_role_model_content_and_usage()
    {
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        await obs.OnAssistantTurnAsync(
            text: "hello world",
            toolCalls: ImmutableArray<ToolCall>.Empty,
            model: "qwen-coder-30b",
            inputTokens: 1234,
            outputTokens: 56,
            ct: CancellationToken.None);

        var events = ParseNdjson(sw.ToString());
        events.Should().HaveCount(1);

        var ev = events[0];
        ev.GetProperty("type").GetString().Should().Be("assistant");

        // The exact path AppSec-Automator's StreamJsonResult.php walks:
        //   $event['message']['usage']['input_tokens']
        //   $event['message']['model']
        var message = ev.GetProperty("message");
        message.GetProperty("role").GetString().Should().Be("assistant");
        message.GetProperty("model").GetString().Should().Be("qwen-coder-30b");
        message.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(1234);
        message.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(56);

        // Content carries one text block when there were no tool calls.
        var content = message.GetProperty("content");
        content.GetArrayLength().Should().Be(1);
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("hello world");
    }

    [Fact]
    public async Task Assistant_event_carries_tool_use_blocks_with_parsed_input()
    {
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        var toolCalls = ImmutableArray.Create(
            new ToolCall("call_1", "Read", """{"file_path":"src/Program.cs"}"""),
            new ToolCall("call_2", "Glob", """{"pattern":"**/*.cs"}"""));

        await obs.OnAssistantTurnAsync(
            text: "I'll read these.",
            toolCalls: toolCalls,
            model: "claude-sonnet-4-6",
            inputTokens: 500, outputTokens: 30, ct: CancellationToken.None);

        var ev = ParseNdjson(sw.ToString())[0];
        var content = ev.GetProperty("message").GetProperty("content");
        content.GetArrayLength().Should().Be(3);

        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("I'll read these.");

        content[1].GetProperty("type").GetString().Should().Be("tool_use");
        content[1].GetProperty("id").GetString().Should().Be("call_1");
        content[1].GetProperty("name").GetString().Should().Be("Read");
        // input must be embedded as a JSON object, not a stringified blob.
        content[1].GetProperty("input").GetProperty("file_path").GetString().Should().Be("src/Program.cs");

        content[2].GetProperty("name").GetString().Should().Be("Glob");
        content[2].GetProperty("input").GetProperty("pattern").GetString().Should().Be("**/*.cs");
    }

    [Fact]
    public async Task Assistant_event_with_no_text_emits_only_tool_use_blocks()
    {
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        await obs.OnAssistantTurnAsync(
            text: "",
            toolCalls: ImmutableArray.Create(new ToolCall("c1", "Read", "{}")),
            model: "m", inputTokens: 0, outputTokens: 0, ct: CancellationToken.None);

        var content = ParseNdjson(sw.ToString())[0].GetProperty("message").GetProperty("content");
        content.GetArrayLength().Should().Be(1);
        content[0].GetProperty("type").GetString().Should().Be("tool_use");
    }

    [Fact]
    public async Task Result_event_publishes_subtype_is_error_num_turns_stop_reason_and_totals()
    {
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        await obs.OnResultAsync(
            subtype: "success",
            isError: false,
            numTurns: 4,
            stopReason: "end_turn",
            resultText: "all done",
            totalInputTokens: 8000,
            totalOutputTokens: 250,
            ct: CancellationToken.None);

        var ev = ParseNdjson(sw.ToString())[0];
        ev.GetProperty("type").GetString().Should().Be("result");
        ev.GetProperty("subtype").GetString().Should().Be("success");
        ev.GetProperty("is_error").GetBoolean().Should().BeFalse();
        ev.GetProperty("num_turns").GetInt32().Should().Be(4);
        ev.GetProperty("stop_reason").GetString().Should().Be("end_turn");
        ev.GetProperty("result").GetString().Should().Be("all done");
        // Flat-token fields kept for back-compat with previously released zdt builds.
        ev.GetProperty("input_tokens").GetInt32().Should().Be(8000);
        ev.GetProperty("output_tokens").GetInt32().Should().Be(250);
        // total_cost_usd is always emitted but always null — LiteLLM doesn't surface it.
        ev.GetProperty("total_cost_usd").ValueKind.Should().Be(JsonValueKind.Null);

        // Nested usage object mirrors claude-cli — the path the official @anthropic-ai/claude-code
        // SDK walks. cache_* default to 0 because LiteLLM doesn't surface prompt caching on the
        // OpenAI-compatible chat endpoint, but the keys are still present so consumers that
        // require all four don't branch on missing.
        var usage = ev.GetProperty("usage");
        usage.GetProperty("input_tokens").GetInt32().Should().Be(8000);
        usage.GetProperty("output_tokens").GetInt32().Should().Be(250);
        usage.GetProperty("cache_creation_input_tokens").GetInt32().Should().Be(0);
        usage.GetProperty("cache_read_input_tokens").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Result_event_can_carry_error_max_turns_subtype()
    {
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        await obs.OnResultAsync(
            subtype: "error_max_turns", isError: true, numTurns: 30,
            stopReason: "max_turns", resultText: null,
            totalInputTokens: 100_000, totalOutputTokens: 1_000, ct: CancellationToken.None);

        var ev = ParseNdjson(sw.ToString())[0];
        ev.GetProperty("subtype").GetString().Should().Be("error_max_turns");
        ev.GetProperty("is_error").GetBoolean().Should().BeTrue();
        ev.GetProperty("stop_reason").GetString().Should().Be("max_turns");
        ev.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Per_delta_methods_are_silent_in_stream_json_mode()
    {
        // Earlier zdt builds emitted text_delta / tool_call / tool_result per event. claude
        // doesn't, and AppSec-Automator only reads "assistant" + "result" — so the per-delta
        // hooks must NOT emit anything in the stream-json sink. (Verbose mode handles the
        // human trace separately on stderr.)
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        await obs.OnTextDeltaAsync("hello ", CancellationToken.None);
        await obs.OnTextDeltaAsync("world", CancellationToken.None);
        await obs.OnToolCallAsync("Read", "{}", CancellationToken.None);
        await obs.OnToolResultAsync("Read", "x", false, TimeSpan.Zero, CancellationToken.None);
        await obs.OnFinalAsync("done", 1, 100, 5, CancellationToken.None);

        sw.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Tool_use_with_invalid_args_json_falls_back_to_string_input()
    {
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        await obs.OnAssistantTurnAsync(
            text: "",
            toolCalls: ImmutableArray.Create(new ToolCall("c1", "Bash", "not-actually-json")),
            model: "m", inputTokens: 0, outputTokens: 0, ct: CancellationToken.None);

        var content = ParseNdjson(sw.ToString())[0].GetProperty("message").GetProperty("content");
        // input must still be present, falling back to the raw string.
        content[0].GetProperty("input").GetString().Should().Be("not-actually-json");
    }
}
