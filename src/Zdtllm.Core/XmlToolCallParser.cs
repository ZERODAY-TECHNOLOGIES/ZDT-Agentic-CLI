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

    // GLM-5.x native emission inside <tool_call>: a bare function name, then one or more
    // <arg_key>K</arg_key><arg_value>V</arg_value> pairs. Matches the raw chat template GLM reverts
    // to even when asked for the <function_calls> dialect. Used only as a last resort in
    // ExtractHermes when the JSON and <function=..> forms yield nothing.
    [GeneratedRegex(@"<arg_key>\s*(.*?)\s*</arg_key>\s*<arg_value>(.*?)</arg_value>", RegexOptions.Singleline)]
    private static partial Regex GlmArgPairRegex();

    public static IReadOnlyList<ParsedXmlToolCall> ExtractCalls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var sanitized = StripThinkingPreamble(text!);

        var calls = new List<ParsedXmlToolCall>();

        foreach (Match block in OpenHandsBlockRegex().Matches(sanitized))
            ExtractOpenHands(block.Groups[1].Value, calls);

        foreach (Match block in HermesBlockRegex().Matches(sanitized))
            ExtractHermes(block.Groups[1].Value, calls);

        // Recovery path: when the upstream proxy or chat template truncates the OPEN tag
        // (we still see "</function_calls>" or "</tool_call>" but the matching open tag is
        // missing or corrupted into something like "_calls>"), the strict regexes above
        // return no matches and the model's intent gets dropped silently. Fall through to a
        // lenient salvage that anchors on the close tag and walks backwards to find an
        // <invoke ...> or <function= start. Empty result still means "really nothing here".
        if (calls.Count == 0 && LooksLikeBrokenXml(sanitized))
        {
            ExtractFromTruncatedOpenTag(sanitized, calls);
        }

        return calls;
    }

    /// <summary>
    /// Heuristic: does <paramref name="text"/> contain XML tool-call markup whose open tag
    /// got corrupted by an upstream pipeline? Used by AgentLoop to decide whether to flag a
    /// format_breakdown event in stream-json (so consumers can distinguish "model deliberately
    /// produced text only" from "model produced tool calls but the wire layer ate the open tag").
    ///
    /// True when we find a close tag of either dialect AND there's no matching opening at all,
    /// OR we see a stray <c>&lt;invoke name=</c> / <c>&lt;function=</c> marker without a
    /// surrounding wrapper. The bar is intentionally low — false positives just emit one extra
    /// warning that downstream logic can still ignore; false negatives leave the bug invisible.
    /// </summary>
    public static bool LooksLikeBrokenXml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = StripThinkingPreamble(text!);

        var hasFcClose = s.Contains("</function_calls>", StringComparison.Ordinal);
        var hasFcOpen  = s.Contains("<function_calls>", StringComparison.Ordinal);
        var hasTcClose = s.Contains("</tool_call>", StringComparison.Ordinal);
        var hasTcOpen  = s.Contains("<tool_call>", StringComparison.Ordinal);

        if (hasFcClose && !hasFcOpen) return true;
        if (hasTcClose && !hasTcOpen) return true;

        // Stray invoke/function markers with no wrapper at all — happens when both sides of the
        // wrapper got chewed up. The strict extractor wouldn't pick these up either, so it's
        // genuinely a breakdown signal even though no close tag survives.
        if (!hasFcClose && !hasTcClose &&
            (s.Contains("<invoke name=", StringComparison.Ordinal)
             || s.Contains("<function=", StringComparison.Ordinal)))
        {
            return true;
        }

        // Backstop: a well-formed <tool_call>…</tool_call> whose body matches NONE of the recognized
        // inner shapes (JSON object, <function=..>, GLM <arg_key>, <invoke>) extracts to zero calls
        // and would be dropped silently. Flag it so format_breakdown telemetry fires and the unknown
        // shape becomes visible instead of vanishing. (AgentLoop only calls this when 0 calls were
        // extracted, so a recognized-but-parsed block never reaches here.)
        foreach (Match block in HermesBlockRegex().Matches(s))
        {
            var body = block.Groups[1].Value.Trim();
            if (body.Length == 0) continue;
            var recognized = body.StartsWith('{')
                || body.Contains("<function=", StringComparison.Ordinal)
                || body.Contains("<arg_key>", StringComparison.Ordinal)
                || body.Contains("<invoke", StringComparison.Ordinal);
            if (!recognized) return true;
        }

        return false;
    }

    /// <summary>
    /// Salvage path: scan for any close tag of either dialect, take everything up to it as the
    /// block body, and run the dialect-appropriate inner extractor. Doesn't care whether the
    /// open tag is missing, partial, or replaced with corruption text — only the inner
    /// <c>&lt;invoke&gt;</c> / <c>&lt;function=&gt;</c> structure has to be intact, which it
    /// almost always is in real-world breakages.
    /// </summary>
    private static void ExtractFromTruncatedOpenTag(string sanitized, List<ParsedXmlToolCall> calls)
    {
        // function_calls dialect — walk every close tag and treat the slice from the previous
        // block end (or start of text) up to the close as the block body. Disjoint-by-construction,
        // so two blocks in a row don't double-count their inner calls.
        SalvageDialect(sanitized, "</function_calls>", calls, ExtractOpenHands);

        // tool_call dialect — same scan with the Hermes extractor.
        SalvageDialect(sanitized, "</tool_call>", calls, ExtractHermes);

        // No close tag at all but stray invoke/function markers — try inner extractors directly
        // on the whole text. The inner regexes match standalone <invoke .../> and <function=...>
        // so this catches the worst-case "both wrapper tags eaten" breakdown.
        if (calls.Count == 0)
        {
            ExtractOpenHands(sanitized, calls);
            ExtractHermes(sanitized, calls);
        }
    }

    private static void SalvageDialect(
        string text,
        string closeTag,
        List<ParsedXmlToolCall> calls,
        Action<string, List<ParsedXmlToolCall>> innerExtractor)
    {
        var blockStart = 0;
        while (true)
        {
            var closeIdx = text.IndexOf(closeTag, blockStart, StringComparison.Ordinal);
            if (closeIdx < 0) break;
            var body = text[blockStart..closeIdx];
            innerExtractor(body, calls);
            blockStart = closeIdx + closeTag.Length;
        }
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

        // Salvage strip: same broken-open-tag case the recovery extractor handles. Without
        // this, a truncated-open block leaves raw "_calls>...<invoke...></function_calls>"
        // markup in the displayed text. Drop the surviving markers individually so we don't
        // accidentally strip legitimate prose that happens to sit before/after the corruption.
        s = OrphanOpenHandsCloseRegex().Replace(s, string.Empty);
        s = OrphanHermesCloseRegex().Replace(s, string.Empty);
        s = OrphanInvokeRegex().Replace(s, string.Empty);
        s = OrphanHermesFunctionRegex().Replace(s, string.Empty);
        s = TruncatedOpenSignatureRegex().Replace(s, string.Empty);

        return s;
    }

    [GeneratedRegex(@"</function_calls>")]
    private static partial Regex OrphanOpenHandsCloseRegex();

    [GeneratedRegex(@"</tool_call>")]
    private static partial Regex OrphanHermesCloseRegex();

    /// <summary>Standalone <c>&lt;invoke&gt;...&lt;/invoke&gt;</c> outside any wrapper.</summary>
    [GeneratedRegex("""<invoke\s+name\s*=\s*"[^"]+"\s*>.*?</invoke>""", RegexOptions.Singleline)]
    private static partial Regex OrphanInvokeRegex();

    /// <summary>Standalone <c>&lt;function=NAME&gt;...&lt;/function&gt;</c> outside any wrapper.</summary>
    [GeneratedRegex(@"<function\s*=\s*[^>\s]+\s*>.*?</function>", RegexOptions.Singleline)]
    private static partial Regex OrphanHermesFunctionRegex();

    /// <summary>
    /// Common upstream-corruption signatures: leading bytes of the open tag got eaten and we
    /// see the trailing fragment instead. The most frequent shapes in the wild are
    /// <c>_calls&gt;</c> (from a chewed <c>&lt;function_calls&gt;</c>) and
    /// <c>_call&gt;</c> (from a chewed <c>&lt;tool_call&gt;</c>) — both at the start of a line.
    /// </summary>
    [GeneratedRegex(@"(?:^|\n)_calls?>\s*", RegexOptions.None)]
    private static partial Regex TruncatedOpenSignatureRegex();

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
        var before = calls.Count;
        foreach (Match func in HermesFunctionRegex().Matches(blockBody))
        {
            var name = func.Groups[1].Value.Trim();
            var body = func.Groups[2].Value;
            calls.Add(BuildCall(name, body, HermesParamRegex()));
        }
        if (calls.Count > before) return;

        // GLM-5.x native form (last resort — only when neither JSON nor <function=..> matched):
        //   <tool_call>NAME<arg_key>k</arg_key><arg_value>v</arg_value>...</tool_call>
        // The name is the leading token before the first <arg_key>; each pair becomes one argument.
        var argMatches = GlmArgPairRegex().Matches(blockBody);
        if (argMatches.Count == 0) return;
        var firstKeyIdx = blockBody.IndexOf("<arg_key>", StringComparison.Ordinal);
        var glmName = blockBody[..firstKeyIdx].Trim();
        if (glmName.Length == 0) return;
        calls.Add(BuildCallFromGlmArgs(glmName, argMatches));
    }

    private static ParsedXmlToolCall BuildCallFromGlmArgs(string name, MatchCollection argPairs)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (Match pair in argPairs)
            {
                w.WritePropertyName(pair.Groups[1].Value.Trim());
                WriteParameterValue(w, pair.Groups[2].Value); // JSON-valued arg_value embeds as JSON
            }
            w.WriteEndObject();
        }
        return new ParsedXmlToolCall(name, Encoding.UTF8.GetString(ms.ToArray()));
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
