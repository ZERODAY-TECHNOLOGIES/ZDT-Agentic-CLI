namespace Zdtllm.Core;

public enum ToolCallingMode
{
    /// <summary>
    /// Send tools via OpenAI-style `tools` array; consume native `tool_calls`
    /// deltas from the response. Use with models that have native function-calling
    /// (most OpenAI/Anthropic/Hermes-templated Qwen models).
    /// </summary>
    Native,

    /// <summary>
    /// Don't send native `tools`. Describe the tool catalog inside the system
    /// prompt and parse OpenHands-style XML blocks
    /// (<![CDATA[<function_calls><invoke name="X"><parameter name="p">v</parameter></invoke></function_calls>]]>)
    /// out of the streamed assistant text. Tool results are appended back as a
    /// synthetic user turn framed as "EXECUTION RESULT of [Tool]: ...". Use with
    /// raw Qwen / DeepSeek / etc. that lack a tool-calling chat template.
    /// </summary>
    Xml,
}

public static class ToolCallingModeParse
{
    public static ToolCallingMode FromString(string? value, ToolCallingMode fallback = ToolCallingMode.Native)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Trim().ToLowerInvariant() switch
        {
            "native" => ToolCallingMode.Native,
            "xml" or "openhands" or "qwen" => ToolCallingMode.Xml,
            _ => throw new ArgumentException(
                $"Unknown tool-calling mode '{value}'. Expected 'native' or 'xml'."),
        };
    }
}
