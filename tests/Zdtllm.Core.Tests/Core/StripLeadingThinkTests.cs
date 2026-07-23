using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// Start-anchored inline-&lt;think&gt; stripping for deployments that inline reasoning in the content
/// channel. Only a LEADING think block is removed — a &lt;think&gt; later in the text (e.g. legitimate
/// generated markup or a security payload) must survive untouched.
/// </summary>
public sealed class StripLeadingThinkTests
{
    [Fact]
    public void Strips_a_leading_think_block_and_returns_the_visible_remainder()
    {
        var (visible, think) = AgentLoop.StripLeadingThink("<think>plan the fix</think>Here is the answer.");
        visible.Should().Be("Here is the answer.");
        think.Should().Be("plan the fix");
    }

    [Fact]
    public void Tolerates_leading_whitespace_before_the_think_tag()
    {
        var (visible, think) = AgentLoop.StripLeadingThink("\n  <think>x</think>done");
        visible.Should().Be("done");
        think.Should().Be("x");
    }

    [Fact]
    public void Unclosed_leading_think_means_no_visible_text_yet()
    {
        var (visible, think) = AgentLoop.StripLeadingThink("<think>still reasoning with no closer");
        visible.Should().BeEmpty();
        think.Should().Be("still reasoning with no closer");
    }

    [Fact]
    public void A_think_tag_that_is_not_leading_is_left_untouched()
    {
        const string code = "Here is code: <think> is a valid HTML-ish token</think> in this string.";
        var (visible, think) = AgentLoop.StripLeadingThink(code);
        visible.Should().Be(code);   // unchanged
        think.Should().BeEmpty();
    }

    [Fact]
    public void No_think_tag_returns_input_unchanged()
    {
        var (visible, think) = AgentLoop.StripLeadingThink("just a plain answer");
        visible.Should().Be("just a plain answer");
        think.Should().BeEmpty();
    }
}
