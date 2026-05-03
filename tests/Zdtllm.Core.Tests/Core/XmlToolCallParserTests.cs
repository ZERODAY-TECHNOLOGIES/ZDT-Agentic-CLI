using System.Text.Json;
using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

public sealed class XmlToolCallParserTests
{
    [Fact]
    public void Returns_empty_when_no_function_calls_present()
    {
        XmlToolCallParser.ExtractCalls("just a plain assistant reply").Should().BeEmpty();
        XmlToolCallParser.ExtractCalls("").Should().BeEmpty();
        XmlToolCallParser.ExtractCalls(null).Should().BeEmpty();
    }

    [Fact]
    public void Parses_single_invoke_with_string_parameter()
    {
        var text =
            """
            I'll read the file.
            <function_calls>
            <invoke name="Read">
            <parameter name="path">./README.md</parameter>
            </invoke>
            </function_calls>
            """;

        var calls = XmlToolCallParser.ExtractCalls(text);

        calls.Should().HaveCount(1);
        calls[0].FunctionName.Should().Be("Read");

        using var doc = JsonDocument.Parse(calls[0].ArgumentsJson);
        doc.RootElement.GetProperty("path").GetString().Should().Be("./README.md");
    }

    [Fact]
    public void Parses_multiple_invokes_within_one_block()
    {
        var text =
            """
            <function_calls>
            <invoke name="Read"><parameter name="path">a.txt</parameter></invoke>
            <invoke name="Bash"><parameter name="command">echo hi</parameter></invoke>
            </function_calls>
            """;

        var calls = XmlToolCallParser.ExtractCalls(text);

        calls.Select(c => c.FunctionName).Should().Equal("Read", "Bash");
    }

    [Fact]
    public void Parses_multiple_blocks_in_same_text()
    {
        var text =
            """
            <function_calls><invoke name="Read"><parameter name="path">a</parameter></invoke></function_calls>
            some text
            <function_calls><invoke name="Bash"><parameter name="command">b</parameter></invoke></function_calls>
            """;

        var calls = XmlToolCallParser.ExtractCalls(text);

        calls.Select(c => c.FunctionName).Should().Equal("Read", "Bash");
    }

    [Fact]
    public void Trims_whitespace_around_string_parameter_values()
    {
        var text =
            """
            <function_calls>
            <invoke name="Read">
            <parameter name="path">
              ./README.md
            </parameter>
            </invoke>
            </function_calls>
            """;

        var calls = XmlToolCallParser.ExtractCalls(text);
        using var doc = JsonDocument.Parse(calls.Single().ArgumentsJson);
        doc.RootElement.GetProperty("path").GetString().Should().Be("./README.md");
    }

    [Fact]
    public void Numeric_looking_string_stays_a_string_so_paths_are_not_re_typed()
    {
        var text =
            """
            <function_calls>
            <invoke name="Read"><parameter name="path">123</parameter></invoke>
            </function_calls>
            """;

        var calls = XmlToolCallParser.ExtractCalls(text);
        using var doc = JsonDocument.Parse(calls.Single().ArgumentsJson);
        doc.RootElement.GetProperty("path").ValueKind.Should().Be(JsonValueKind.String);
        doc.RootElement.GetProperty("path").GetString().Should().Be("123");
    }

    [Fact]
    public void Object_or_array_literals_are_promoted_to_their_JSON_value()
    {
        var text =
            """
            <function_calls>
            <invoke name="TodoWrite">
            <parameter name="todos">[{"id":1,"text":"do X"}]</parameter>
            </invoke>
            </function_calls>
            """;

        var calls = XmlToolCallParser.ExtractCalls(text);
        using var doc = JsonDocument.Parse(calls.Single().ArgumentsJson);
        var todos = doc.RootElement.GetProperty("todos");
        todos.ValueKind.Should().Be(JsonValueKind.Array);
        todos.EnumerateArray().Single().GetProperty("text").GetString().Should().Be("do X");
    }

    [Fact]
    public void Multiple_parameters_in_one_invoke_are_packed_together()
    {
        var text =
            """
            <function_calls>
            <invoke name="Read">
            <parameter name="path">./big.txt</parameter>
            <parameter name="offset">10</parameter>
            <parameter name="limit">5</parameter>
            </invoke>
            </function_calls>
            """;

        var calls = XmlToolCallParser.ExtractCalls(text);
        using var doc = JsonDocument.Parse(calls.Single().ArgumentsJson);
        doc.RootElement.GetProperty("path").GetString().Should().Be("./big.txt");
        doc.RootElement.GetProperty("offset").GetString().Should().Be("10");
        doc.RootElement.GetProperty("limit").GetString().Should().Be("5");
    }

    [Fact]
    public void Parses_Hermes_style_tool_call_with_function_equals_syntax()
    {
        // What Qwen2.5/Qwen3 with Hermes chat template emits in native function-calling.
        var text =
            """
            <tool_call>
            <function=Read>
            <parameter=path>./README.md</parameter>
            <parameter=offset>10</parameter>
            </function>
            </tool_call>
            """;

        var calls = XmlToolCallParser.ExtractCalls(text);

        calls.Should().HaveCount(1);
        calls[0].FunctionName.Should().Be("Read");
        using var doc = JsonDocument.Parse(calls[0].ArgumentsJson);
        doc.RootElement.GetProperty("path").GetString().Should().Be("./README.md");
        doc.RootElement.GetProperty("offset").GetString().Should().Be("10");
    }

    [Fact]
    public void Parses_Hermes_style_JSON_inside_tool_call_block()
    {
        // Some Hermes variants emit a JSON object directly:
        var text = """<tool_call>{"name":"Read","arguments":{"path":"./x"}}</tool_call>""";

        var calls = XmlToolCallParser.ExtractCalls(text);

        calls.Should().HaveCount(1);
        calls[0].FunctionName.Should().Be("Read");
        using var doc = JsonDocument.Parse(calls[0].ArgumentsJson);
        doc.RootElement.GetProperty("path").GetString().Should().Be("./x");
    }

    [Fact]
    public void Parses_Hermes_JSON_with_string_encoded_arguments()
    {
        // OpenAI's "function-calling messages" historically encode `arguments` as a
        // JSON-stringified blob; a few Hermes-derivatives mirror that.
        var text = """<tool_call>{"name":"Bash","arguments":"{\"command\":\"echo hi\"}"}</tool_call>""";

        var calls = XmlToolCallParser.ExtractCalls(text);

        calls.Should().HaveCount(1);
        calls[0].FunctionName.Should().Be("Bash");
        using var doc = JsonDocument.Parse(calls[0].ArgumentsJson);
        doc.RootElement.GetProperty("command").GetString().Should().Be("echo hi");
    }

    [Fact]
    public void Strip_removes_both_OpenHands_and_Hermes_blocks()
    {
        var text =
            """
            Lead-in.
            <function_calls><invoke name="A"><parameter name="x">1</parameter></invoke></function_calls>
            Middle.
            <tool_call><function=B><parameter=y>2</parameter></function></tool_call>
            Trail.
            """;

        var stripped = XmlToolCallParser.Strip(text);
        stripped.Should().Contain("Lead-in.");
        stripped.Should().Contain("Middle.");
        stripped.Should().Contain("Trail.");
        stripped.Should().NotContain("function_calls");
        stripped.Should().NotContain("tool_call");
        stripped.Should().NotContain("<invoke");
        stripped.Should().NotContain("<function=");
    }

    [Fact]
    public void Strip_drops_thinking_preamble_before_close_think_marker()
    {
        // Qwen3 / DeepSeek-R1 emit reasoning followed by </think> followed by the answer.
        var text = "I'm reasoning about the question...\n</think>\n\nThe answer is 42.";

        var stripped = XmlToolCallParser.Strip(text);

        stripped.Should().Contain("The answer is 42");
        stripped.Should().NotContain("reasoning about the question");
        stripped.Should().NotContain("</think>");
    }

    [Fact]
    public void Extract_ignores_function_calls_inside_thinking_preamble()
    {
        // The model is reasoning ABOUT calling a tool but hasn't actually committed
        // to one yet — the </think> marker hasn't appeared, so reasoning continues.
        var text =
            """
            I should probably call <function_calls><invoke name="Read"><parameter name="path">a</parameter></invoke></function_calls>
            but on second thought let me not.
            </think>
            Final answer.
            """;

        XmlToolCallParser.ExtractCalls(text).Should().BeEmpty();
    }

    [Fact]
    public void Extract_uses_calls_emitted_after_thinking_preamble()
    {
        var text =
            """
            reasoning blah blah
            </think>
            <function_calls>
            <invoke name="Read"><parameter name="path">x</parameter></invoke>
            </function_calls>
            """;

        var calls = XmlToolCallParser.ExtractCalls(text);
        calls.Should().HaveCount(1);
        calls[0].FunctionName.Should().Be("Read");
    }

    [Fact]
    public void Strip_removes_function_calls_blocks_keeping_surrounding_text()
    {
        var text =
            """
            Here is the answer:
            <function_calls>
            <invoke name="X"><parameter name="p">v</parameter></invoke>
            </function_calls>
            (call dispatched)
            """;

        var stripped = XmlToolCallParser.Strip(text);
        stripped.Should().Contain("Here is the answer:");
        stripped.Should().Contain("(call dispatched)");
        stripped.Should().NotContain("<invoke");
        stripped.Should().NotContain("function_calls");
    }
}
