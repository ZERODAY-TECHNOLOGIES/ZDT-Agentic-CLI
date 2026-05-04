using Zdtllm.Core;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// Covers AgentLoop.SplitConcatenatedArgs — the defensive parser that rewrites a single
/// ToolCall whose `arguments` are multiple JSON objects glued together (the GLM-5 parallel
/// tool-calls bug: <c>{"x":1}{"y":2}{"z":3}</c>) into N separate ToolCalls with the same
/// function name. Single-object args MUST pass through unchanged so non-buggy models are
/// not affected.
/// </summary>
public sealed class SplitConcatenatedArgsTests
{
    [Fact]
    public void Single_valid_json_object_passes_through_untouched()
    {
        var call = new ToolCall("c1", "Read", """{"path":"/etc/passwd"}""");

        var result = AgentLoop.SplitConcatenatedArgs(call);

        result.Should().HaveCount(1);
        result[0].Should().BeSameAs(call);
    }

    [Fact]
    public void Empty_args_pass_through_untouched()
    {
        var call = new ToolCall("c1", "List", "");

        var result = AgentLoop.SplitConcatenatedArgs(call);

        result.Should().ContainSingle().Which.Should().BeSameAs(call);
    }

    [Fact]
    public void Concatenated_two_objects_become_two_calls()
    {
        var call = new ToolCall("c1", "Glob", """{"pattern":"**/*.cs"}{"pattern":"**/*.json"}""");

        var result = AgentLoop.SplitConcatenatedArgs(call);

        result.Should().HaveCount(2);
        result[0].FunctionName.Should().Be("Glob");
        result[0].Arguments.Should().Be("""{"pattern":"**/*.cs"}""");
        result[0].Id.Should().Be("c1_s0");
        result[1].FunctionName.Should().Be("Glob");
        result[1].Arguments.Should().Be("""{"pattern":"**/*.json"}""");
        result[1].Id.Should().Be("c1_s1");
    }

    [Fact]
    public void Concatenated_five_objects_with_whitespace_split_correctly()
    {
        // The exact GLM-5 SAST pattern that triggered this work — five Glob patterns
        // glued without separators, plus realistic newlines/spaces between objects.
        var call = new ToolCall("c1", "Glob",
            "{\"pattern\":\"**/*.cs\"}  {\"pattern\":\"**/*.csproj\"}{\"pattern\":\"**/*.json\"}\n" +
            "{\"pattern\":\"**/*.py\"}{\"pattern\":\"**/*.js\"}");

        var result = AgentLoop.SplitConcatenatedArgs(call);

        result.Should().HaveCount(5);
        result.Select(r => r.FunctionName).Should().AllBe("Glob");
        result.Select(r => r.Arguments).Should().Equal(
            """{"pattern":"**/*.cs"}""",
            """{"pattern":"**/*.csproj"}""",
            """{"pattern":"**/*.json"}""",
            """{"pattern":"**/*.py"}""",
            """{"pattern":"**/*.js"}""");
    }

    [Fact]
    public void Nested_objects_split_at_outer_boundary_only()
    {
        // Args with nested {} must split only at top-level — inner braces stay intact.
        var call = new ToolCall("c1", "Tool",
            """{"a":{"b":1}}{"c":{"d":{"e":2}}}""");

        var result = AgentLoop.SplitConcatenatedArgs(call);

        result.Should().HaveCount(2);
        result[0].Arguments.Should().Be("""{"a":{"b":1}}""");
        result[1].Arguments.Should().Be("""{"c":{"d":{"e":2}}}""");
    }

    [Fact]
    public void String_containing_braces_does_not_trigger_false_split()
    {
        // A single object whose string value contains literal `{` / `}` — JSON parse
        // succeeds → quick path → original returned untouched.
        var call = new ToolCall("c1", "Echo", """{"text":"a {literal} {brace} value"}""");

        var result = AgentLoop.SplitConcatenatedArgs(call);

        result.Should().ContainSingle().Which.Should().BeSameAs(call);
    }

    [Fact]
    public void Truncated_args_pass_through_untouched()
    {
        // Arguments cut off mid-stream (truly malformed, NOT "Extra data" error) must not
        // be turned into a split — we'd be inventing structure that isn't there. The tool
        // gets the original string and surfaces its own diagnostic.
        var call = new ToolCall("c1", "Read", """{"path":"/etc/pas""");

        var result = AgentLoop.SplitConcatenatedArgs(call);

        result.Should().ContainSingle().Which.Should().BeSameAs(call);
    }

    [Fact]
    public void Concatenated_with_one_invalid_chunk_falls_back_to_original()
    {
        // If a top-level slice doesn't parse on its own, the whole split is abandoned —
        // we'd rather pass through the original (and let the tool error out clearly) than
        // synthesise N broken sub-calls.
        var call = new ToolCall("c1", "Tool", """{"valid":1}{not json}""");

        var result = AgentLoop.SplitConcatenatedArgs(call);

        result.Should().ContainSingle().Which.Should().BeSameAs(call);
    }

    [Fact]
    public void Single_object_with_quoted_close_brace_does_not_false_split()
    {
        // Edge case: an arg string contains `}{` literally inside a JSON string value.
        // The full string parses as one valid JSON, so we never hit the split path.
        var call = new ToolCall("c1", "Echo", """{"text":"close-then-open: }{"}""");

        var result = AgentLoop.SplitConcatenatedArgs(call);

        result.Should().ContainSingle().Which.Should().BeSameAs(call);
    }
}
