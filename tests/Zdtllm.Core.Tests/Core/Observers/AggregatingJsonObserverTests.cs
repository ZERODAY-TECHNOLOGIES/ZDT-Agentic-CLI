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
