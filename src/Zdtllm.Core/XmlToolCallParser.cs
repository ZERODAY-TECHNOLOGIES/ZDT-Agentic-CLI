using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zdtllm.Core;

public sealed record ParsedXmlToolCall(string FunctionName, string ArgumentsJson);

/// <summary>
/// Parses OpenHands-style XML tool calls embedded in assistant text, e.g.
/// <code>
/// &lt;function_calls&gt;
/// &lt;invoke name="Read"&gt;
/// &lt;parameter name="path"&gt;./README.md&lt;/parameter&gt;
/// &lt;/invoke&gt;
/// &lt;/function_calls&gt;
/// </code>
/// Parameters are emitted as JSON strings by default; only literals that already
/// look like JSON objects/arrays are promoted to their structural form. Numbers and
/// booleans stay as strings here so that a path like "123" or "true" is not silently
/// re-typed; tools coerce string→int/bool at the receiving end.
/// </summary>
public static partial class XmlToolCallParser
{
    [GeneratedRegex(@"<function_calls>(.*?)</function_calls>", RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex("""<invoke\s+name\s*=\s*"([^"]+)"\s*>(.*?)</invoke>""", RegexOptions.Singleline)]
    private static partial Regex InvokeRegex();

    [GeneratedRegex("""<parameter\s+name\s*=\s*"([^"]+)"\s*>(.*?)</parameter>""", RegexOptions.Singleline)]
    private static partial Regex ParamRegex();

    public static IReadOnlyList<ParsedXmlToolCall> ExtractCalls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (!text!.Contains("<function_calls>", StringComparison.Ordinal)) return [];

        var calls = new List<ParsedXmlToolCall>();
        foreach (Match block in BlockRegex().Matches(text))
        {
            var inner = block.Groups[1].Value;
            foreach (Match invoke in InvokeRegex().Matches(inner))
            {
                var name = invoke.Groups[1].Value;
                var body = invoke.Groups[2].Value;

                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    foreach (Match param in ParamRegex().Matches(body))
                    {
                        var pName = param.Groups[1].Value;
                        var pValue = param.Groups[2].Value;
                        w.WritePropertyName(pName);
                        WriteParameterValue(w, pValue);
                    }
                    w.WriteEndObject();
                }
                calls.Add(new ParsedXmlToolCall(name, Encoding.UTF8.GetString(ms.ToArray())));
            }
        }
        return calls;
    }

    /// <summary>
    /// Returns the input text with all <function_calls>...</function_calls> blocks removed.
    /// Used to clean assistant text before showing it to the user.
    /// </summary>
    public static string Strip(string text) =>
        string.IsNullOrEmpty(text) ? text : BlockRegex().Replace(text, string.Empty);

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
