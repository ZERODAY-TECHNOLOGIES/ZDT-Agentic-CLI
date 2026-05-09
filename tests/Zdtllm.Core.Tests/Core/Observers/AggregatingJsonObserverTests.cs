using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Observers;

namespace Zdtllm.Core.Tests.Core.Observers;

public sealed class AggregatingJsonObserverTests
{
    [Fact]
    public async Task Emits_a_single_pretty_json_object_with_pairing_calls_to_results()
    {
        var agg = new AggregatingJsonObserver();
        IAgentObserver obs = agg;

        await obs.OnToolCallAsync("Read", """{"file_path":"a.txt"}""", CancellationToken.None);
        await obs.OnToolResultAsync("Read", "alpha", false, TimeSpan.FromMilliseconds(10), CancellationToken.None);
        await obs.OnToolCallAsync("Read", """{"file_path":"b.txt"}""", CancellationToken.None);
        await obs.OnToolResultAsync("Read", "bravo", false, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        await obs.OnTextDeltaAsync("done", CancellationToken.None);
        await obs.OnFinalAsync("done", turns: 2, promptTokens: 250, completionTokens: 4, CancellationToken.None);

        var sw = new StringWriter();
        await agg.EmitAsync(sw, CancellationToken.None);

        using var doc = JsonDocument.Parse(sw.ToString());
        var root = doc.RootElement;
        root.GetProperty("result").GetString().Should().Be("done");
        root.GetProperty("turns").GetInt32().Should().Be(2);
        root.GetProperty("prompt_tokens").GetInt32().Should().Be(250);

        var calls = root.GetProperty("tool_calls");
        calls.GetArrayLength().Should().Be(2);
        calls[0].GetProperty("arguments").GetProperty("file_path").GetString().Should().Be("a.txt");
        calls[0].GetProperty("content").GetString().Should().Be("alpha");
        calls[0].GetProperty("duration_ms").GetInt64().Should().Be(10);
        calls[1].GetProperty("arguments").GetProperty("file_path").GetString().Should().Be("b.txt");
        calls[1].GetProperty("content").GetString().Should().Be("bravo");
    }

    [Fact]
    public async Task Result_payload_carries_tool_error_count_and_had_tool_errors_in_sync_with_stream_json()
    {
        // Symmetry with StreamJsonObserver: --output-format json consumers shouldn't
        // have to switch formats to read the same tool-error telemetry.
        var agg = new AggregatingJsonObserver();
        IAgentObserver obs = agg;

        await obs.OnToolCallAsync("start_step", "{}", CancellationToken.None);
        await obs.OnToolResultAsync("start_step", "[Unknown tool: start_step]", true,
            TimeSpan.FromMilliseconds(1), CancellationToken.None);
        await obs.OnFinalAsync("gave up", turns: 1, promptTokens: 100, completionTokens: 5, CancellationToken.None);
        await obs.OnResultAsync(
            subtype: "success", isError: false, numTurns: 1,
            stopReason: "end_turn", resultText: "gave up",
            totalInputTokens: 100, totalOutputTokens: 5,
            ct: CancellationToken.None,
            formatBreakdown: false,
            toolErrorCount: 1);

        var sw = new StringWriter();
        await agg.EmitAsync(sw, CancellationToken.None);

        using var doc = JsonDocument.Parse(sw.ToString());
        var root = doc.RootElement;
        root.GetProperty("tool_error_count").GetInt32().Should().Be(1);
        root.GetProperty("had_tool_errors").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Tool_error_fields_default_to_zero_and_false_when_OnResult_is_never_called()
    {
        // Earlier tests (and live runs that crash before OnResultAsync fires) must keep
        // working — the defaults give consumers stable fields to read.
        var agg = new AggregatingJsonObserver();
        IAgentObserver obs = agg;
        await obs.OnFinalAsync("ok", 1, 10, 1, CancellationToken.None);

        var sw = new StringWriter();
        await agg.EmitAsync(sw, CancellationToken.None);

        using var doc = JsonDocument.Parse(sw.ToString());
        doc.RootElement.GetProperty("tool_error_count").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("had_tool_errors").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Result_field_falls_back_to_streamed_text_when_OnFinal_is_never_called()
    {
        // Defensive: if the agent crashes before OnFinal, we should still produce a
        // useful payload from the deltas we DID see.
        var agg = new AggregatingJsonObserver();
        IAgentObserver obs = agg;
        await obs.OnTextDeltaAsync("partial ", CancellationToken.None);
        await obs.OnTextDeltaAsync("answer", CancellationToken.None);

        var sw = new StringWriter();
        await agg.EmitAsync(sw, CancellationToken.None);

        using var doc = JsonDocument.Parse(sw.ToString());
        doc.RootElement.GetProperty("result").GetString().Should().Be("partial answer");
        doc.RootElement.GetProperty("turns").GetInt32().Should().Be(0);
    }
}
