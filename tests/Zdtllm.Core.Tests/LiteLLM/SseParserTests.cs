using System.Text;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.LiteLLM;

public sealed class SseParserTests
{
    private static async Task<List<ChatChunk>> ParseAllAsync(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var chunks = new List<ChatChunk>();
        await foreach (var c in SseParser.ParseAsync(stream))
            chunks.Add(c);
        return chunks;
    }

    [Fact]
    public async Task Yields_text_deltas_in_order()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var chunks = await ParseAllAsync(sse);

        chunks.OfType<ChatChunk.TextDelta>().Select(c => c.Text)
            .Should().Equal("Hello", " world");
    }

    [Fact]
    public async Task Stops_on_DONE_marker()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n" +
            "data: [DONE]\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"never\"}}]}\n\n";

        var chunks = await ParseAllAsync(sse);

        chunks.OfType<ChatChunk.TextDelta>().Should().HaveCount(1);
    }

    [Fact]
    public async Task Yields_tool_call_deltas_with_index_and_id()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Read\",\"arguments\":\"\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"function\":{\"arguments\":\"{\\\"path\\\"\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"function\":{\"arguments\":\":\\\"./README.md\\\"}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n";

        var chunks = await ParseAllAsync(sse);

        var toolCalls = chunks.OfType<ChatChunk.ToolCallDelta>().ToList();
        toolCalls.Should().HaveCount(3);
        toolCalls[0].Should().BeEquivalentTo(new
        {
            Index = 0,
            Id = "call_1",
            FunctionName = "Read",
            ArgumentsDelta = "",
        });
        toolCalls[1].ArgumentsDelta.Should().Be("{\"path\"");
        toolCalls[2].ArgumentsDelta.Should().Be(":\"./README.md\"}");
    }

    [Fact]
    public async Task Emits_Usage_chunk_when_present()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]," +
                "\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":12}}\n\n" +
            "data: [DONE]\n\n";

        var chunks = await ParseAllAsync(sse);

        var usage = chunks.OfType<ChatChunk.Usage>().Single();
        usage.PromptTokens.Should().Be(50);
        usage.CompletionTokens.Should().Be(12);
    }

    [Fact]
    public async Task Emits_Done_chunk_with_finish_reason()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";

        var chunks = await ParseAllAsync(sse);

        chunks.OfType<ChatChunk.Done>().Single().FinishReason.Should().Be("tool_calls");
    }

    [Fact]
    public async Task Tolerates_blank_lines_and_comments()
    {
        var sse =
            ": this is a comment\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n" +
            "\n" +
            "data: [DONE]\n";

        var chunks = await ParseAllAsync(sse);

        chunks.OfType<ChatChunk.TextDelta>().Single().Text.Should().Be("a");
    }

    [Fact]
    public async Task Skips_malformed_json_chunks()
    {
        var sse =
            "data: {not valid json\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"after-bad\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var chunks = await ParseAllAsync(sse);

        chunks.OfType<ChatChunk.TextDelta>().Single().Text.Should().Be("after-bad");
    }
}
