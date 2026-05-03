using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zdtllm.Core;

public sealed record ParsedXmlToolCall(string FunctionName, string ArgumentsJson);

/// <summary>
/// Parses tool calls embedded in assistant text. Two flavors are supported:
///
/// 1. OpenHands / Anthropic-XML style — what we ASK the model to use in our system
///    prompt:
///    <code>
///    &lt;function_calls&gt;
///      &lt;invoke name="Read"&gt;
///        &lt;parameter name="path"&gt;./README.md&lt;/parameter&gt;
///      &lt;/invoke&gt;
///    &lt;/function_calls&gt;
///    </code>
///
/// 2. Hermes / Qwen-Agent style — what most Qwen / Hermes-templated models emit
///    natively (with `=` between tag and name, no quoted attributes):
///    <code>
///    &lt;tool_call&gt;
///      &lt;function=Read&gt;
///        &lt;parameter=path&gt;./README.md&lt;/parameter&gt;
///      &lt;/function&gt;
///    &lt;/tool_call&gt;
///    </code>
///    Some Hermes variants emit JSON inside the tag instead:
///    <code>&lt;tool_call&gt;{"name":"Read","arguments":{"path":"./x"}}&lt;/tool_call&gt;</code>
///
/// Parameter values stay as JSON strings unless the literal already looks like a
/// JSON object/array, so a path like "123" or "true" is not silently re-typed;
/// tools coerce string→int/bool at the receiving end.
///
/// Reasoning preambles (`...</think>`) emitted by Qwen3 / DeepSeek-R1 thinking
/// models are stripped before parsing so reasoning that mentions tools cannot
/// be misread as a real call.
/// </summary>
public static partial class XmlToolCallParser
{
    [GeneratedRegex(@"<function_calls>(.*?)</function_calls>", RegexOptions.Singleline)]
    private static partial Regex OpenHandsBlockRegex();

    [GeneratedRegex(@"<tool_call>(.*?)</tool_call>", RegexOptions.Singleline)]
    private static partial Regex HermesBlockRegex();

    [GeneratedRegex("""<invoke\s+name\s*=\s*"([^"]+)"\s*>(.*?)</invoke>""", RegexOptions.Singleline)]
    private static partial Regex InvokeRegex();

    [GeneratedRegex("""<parameter\s+name\s*=\s*"([^"]+)"\s*>(.*?)</parameter>""", RegexOptions.Singleline)]
    private static partial Regex OpenHandsParamRegex();

    [GeneratedRegex(@"<function\s*=\s*([^>\s]+)\s*>(.*?)</function>", RegexOptions.Singleline)]
    private static partial Regex HermesFunctionRegex();

    [GeneratedRegex(@"<parameter\s*=\s*([^>\s]+)\s*>(.*?)</parameter>", RegexOptions.Singleline)]
    private static partial Regex HermesParamRegex();

    public static IReadOnlyList<ParsedXmlToolCall> ExtractCalls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var sanitized = StripThinkingPreamble(text!);

        var calls = new List<ParsedXmlToolCall>();

        foreach (Match block in OpenHandsBlockRegex().Matches(sanitized))
            ExtractOpenHands(block.Groups[1].Value, calls);

        foreach (Match block in HermesBlockRegex().Matches(sanitized))
            ExtractHermes(block.Groups[1].Value, calls);

        return calls;
    }

    /// <summary>
    /// Returns the input text with reasoning preambles and both flavors of tool-call
    /// blocks (function_calls and tool_call) removed. Used to clean assistant text
    /// before showing it to the user.
    /// </summary>
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = StripThinkingPreamble(text);
        s = OpenHandsBlockRegex().Replace(s, string.Empty);
        s = HermesBlockRegex().Replace(s, string.Empty);
        return s;
    }

    private static string StripThinkingPreamble(string text)
    {
        const string Closer = "</think>";
        var idx = text.IndexOf(Closer, StringComparison.Ordinal);
        return idx < 0 ? text : text[(idx + Closer.Length)..];
    }

    private static void ExtractOpenHands(string blockBody, List<ParsedXmlToolCall> calls)
    {
        foreach (Match invoke in InvokeRegex().Matches(blockBody))
        {
            var name = invoke.Groups[1].Value;
            var body = invoke.Groups[2].Value;
            calls.Add(BuildCall(name, body, OpenHandsParamRegex()));
        }
    }

    private static void ExtractHermes(string blockBody, List<ParsedXmlToolCall> calls)
    {
        var trimmed = blockBody.Trim();

        // JSON form: {"name": "X", "arguments": {...}}
        if (trimmed.StartsWith('{'))
        {
            if (TryParseHermesJson(trimmed, out var jsonCall))
            {
                calls.Add(jsonCall);
                return;
            }
        }

        // Tag form: <function=NAME>...<parameter=NAME>VALUE</parameter>...</function>
        foreach (Match func in HermesFunctionRegex().Matches(blockBody))
        {
            var name = func.Groups[1].Value.Trim();
            var body = func.Groups[2].Value;
            calls.Add(BuildCall(name, body, HermesParamRegex()));
        }
    }

    private static bool TryParseHermesJson(string body, out ParsedXmlToolCall call)
    {
        call = null!;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return false;

            string argsJson;
            if (root.TryGetProperty("arguments", out var args))
            {
                // arguments may be an object OR a JSON-encoded string of an object.
                argsJson = args.ValueKind switch
                {
                    JsonValueKind.Object or JsonValueKind.Array => args.GetRawText(),
                    JsonValueKind.String => args.GetString() ?? "{}",
                    _ => "{}",
                };
            }
            else
            {
                argsJson = "{}";
            }

            call = new ParsedXmlToolCall(n.GetString()!, argsJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ParsedXmlToolCall BuildCall(string name, string body, Regex paramRegex)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (Match param in paramRegex.Matches(body))
            {
                var pName = param.Groups[1].Value;
                var pValue = param.Groups[2].Value;
                w.WritePropertyName(pName);
                WriteParameterValue(w, pValue);
            }
            w.WriteEndObject();
        }
        return new ParsedXmlToolCall(name, Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static void WriteParameterValue(Utf8JsonWriter w, string raw)
    {
        var trimmed = raw.Trim();

        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
            (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                doc.RootElement.WriteTo(w);
                return;
            }
            catch (JsonException)
            {
                // Not actually valid JSON — fall through and treat as string.
            }
        }

        w.WriteStringValue(trimmed);
    }
}
