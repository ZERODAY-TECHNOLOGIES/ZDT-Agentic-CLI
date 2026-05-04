using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Observers;

namespace Zdtllm.Core.Tests.Core.Observers;

/// <summary>
/// StreamJsonObserver should emit one well-formed JSON line per event. Tests parse the
/// resulting NDJSON back to verify shape; this catches escaping bugs that a substring
/// match would let through.
/// </summary>
public sealed class StreamJsonObserverTests
{
    private static IReadOnlyList<JsonElement> ParseNdjson(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    [Fact]
    public async Task Each_event_emits_one_json_line_with_correct_type_and_payload()
    {
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        await obs.OnTextDeltaAsync("hello ", CancellationToken.None);
        await obs.OnTextDeltaAsync("world", CancellationToken.None);
        await obs.OnToolCallAsync("Read", """{"file_path":"x.txt"}""", CancellationToken.None);
        await obs.OnToolResultAsync("Read", "contents", isError: false, TimeSpan.FromMilliseconds(42), CancellationToken.None);
        await obs.OnFinalAsync("hello world", turns: 1, promptTokens: 100, completionTokens: 5, CancellationToken.None);

        var events = ParseNdjson(sw.ToString());
        events.Should().HaveCount(5);

        events[0].GetProperty("type").GetString().Should().Be("text_delta");
        events[0].GetProperty("text").GetString().Should().Be("hello ");

        events[1].GetProperty("text").GetString().Should().Be("world");

        events[2].GetProperty("type").GetString().Should().Be("tool_call");
        events[2].GetProperty("name").GetString().Should().Be("Read");
        // Arguments should be embedded as a JSON OBJECT, not a stringified blob.
        events[2].GetProperty("arguments").GetProperty("file_path").GetString().Should().Be("x.txt");

        events[3].GetProperty("type").GetString().Should().Be("tool_result");
        events[3].GetProperty("name").GetString().Should().Be("Read");
        events[3].GetProperty("content").GetString().Should().Be("contents");
        events[3].GetProperty("is_error").GetBoolean().Should().BeFalse();
        events[3].GetProperty("duration_ms").GetInt64().Should().Be(42);

        events[4].GetProperty("type").GetString().Should().Be("final");
        events[4].GetProperty("text").GetString().Should().Be("hello world");
        events[4].GetProperty("turns").GetInt32().Should().Be(1);
        events[4].GetProperty("prompt_tokens").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task Empty_text_delta_does_not_emit_a_line()
    {
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        await obs.OnTextDeltaAsync("", CancellationToken.None);
        await obs.OnTextDeltaAsync(" ", CancellationToken.None); // whitespace IS content
        await obs.OnFinalAsync("done", 1, null, null, CancellationToken.None);

        var events = ParseNdjson(sw.ToString());
        events.Should().HaveCount(2);  // " " delta + final
        events[0].GetProperty("text").GetString().Should().Be(" ");
    }

    [Fact]
    public async Task Tool_call_with_invalid_args_json_falls_back_to_string_payload()
    {
        var sw = new StringWriter();
        IAgentObserver obs = new StreamJsonObserver(sw);

        await obs.OnToolCallAsync("Bash", "not-actually-json", CancellationToken.None);

        var events = ParseNdjson(sw.ToString());
        events[0].GetProperty("arguments").GetString().Should().Be("not-actually-json");
    }
}
