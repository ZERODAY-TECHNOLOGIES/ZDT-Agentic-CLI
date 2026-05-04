using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Zdtllm.LiteLLM;

public static class SseParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static async IAsyncEnumerable<ChatChunk> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) yield break;
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line.AsSpan("data:".Length).TrimStart().ToString();
            if (data.Length == 0) continue;
            if (data == "[DONE]") yield break;

            var parsed = TryParse(data);
            if (parsed is null) continue;

            foreach (var chunk in ToChunks(parsed))
                yield return chunk;
        }
    }

    private static StreamChunkResponse? TryParse(string data)
    {
        try { return JsonSerializer.Deserialize<StreamChunkResponse>(data, JsonOpts); }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<ChatChunk> ToChunks(StreamChunkResponse parsed)
    {
        if (parsed.Choices is { Count: > 0 } choices)
        {
            foreach (var choice in choices)
            {
                if (choice.Delta?.Content is { Length: > 0 } content)
                    yield return new ChatChunk.TextDelta(content);

                if (choice.Delta?.ReasoningContent is { Length: > 0 } reasoning)
                    yield return new ChatChunk.ReasoningDelta(reasoning);

                if (choice.Delta?.ToolCalls is { Count: > 0 } toolCalls)
                {
                    foreach (var tc in toolCalls)
                    {
                        yield return new ChatChunk.ToolCallDelta(
                            Index: tc.Index,
                            Id: tc.Id,
                            FunctionName: tc.Function?.Name,
                            ArgumentsDelta: tc.Function?.Arguments);
                    }
                }

                if (!string.IsNullOrEmpty(choice.FinishReason))
                    yield return new ChatChunk.Done(choice.FinishReason);
            }
        }

        if (parsed.Usage is not null)
            yield return new ChatChunk.Usage(parsed.Usage.PromptTokens, parsed.Usage.CompletionTokens);
    }
}

internal sealed class StreamChunkResponse
{
    public List<StreamChoice>? Choices { get; set; }
    public StreamUsage? Usage { get; set; }
}

internal sealed class StreamChoice
{
    public int Index { get; set; }
    public StreamDelta? Delta { get; set; }
    public string? FinishReason { get; set; }
}

internal sealed class StreamDelta
{
    public string? Role { get; set; }
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public List<StreamToolCall>? ToolCalls { get; set; }
}

internal sealed class StreamToolCall
{
    public int Index { get; set; }
    public string? Id { get; set; }
    public string? Type { get; set; }
    public StreamFunctionCall? Function { get; set; }
}

internal sealed class StreamFunctionCall
{
    public string? Name { get; set; }
    public string? Arguments { get; set; }
}

internal sealed class StreamUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
}
