using Zdtllm.Core.Workflows;

namespace Zdtllm.Core.Tests.Core.Workflows;

public sealed class WorkflowTemplateTests
{
    [Fact]
    public void Substitutes_known_placeholders()
    {
        var ctx = new Dictionary<string, string> { ["item"] = "a.cs", ["lang"] = "C#" };
        WorkflowTemplate.Resolve("review {{item}} as {{lang}}", ctx)
            .Should().Be("review a.cs as C#");
    }

    [Fact]
    public void Leaves_unknown_placeholders_untouched()
    {
        WorkflowTemplate.Resolve("hi {{missing}} there", new Dictionary<string, string>())
            .Should().Be("hi {{missing}} there");
    }

    [Fact]
    public void Supports_dotted_names_for_phase_results()
    {
        var ctx = new Dictionary<string, string> { ["Review.results"] = "found 3 bugs" };
        WorkflowTemplate.Resolve("summarize: {{Review.results}}", ctx)
            .Should().Be("summarize: found 3 bugs");
    }

    [Fact]
    public void Tolerates_whitespace_inside_braces()
    {
        var ctx = new Dictionary<string, string> { ["x"] = "1" };
        WorkflowTemplate.Resolve("{{ x }}", ctx).Should().Be("1");
    }

    [Fact]
    public void Placeholders_lists_referenced_names()
    {
        WorkflowTemplate.Placeholders("{{a}} and {{b}} and {{a}}")
            .Should().BeEquivalentTo(new[] { "a", "b" });
    }
}
