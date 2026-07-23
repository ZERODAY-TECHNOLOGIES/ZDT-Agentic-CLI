using System.Text.RegularExpressions;
using Zdtllm.Cli;
using Zdtllm.Core.Sessions;

namespace Zdtllm.Core.Tests.Cli;

/// <summary>
/// On resume, the prior conversation is replayed to the terminal so the user sees the context they're
/// continuing. These pin the transcript content/shape (a fresh session prints nothing; user + assistant
/// turns are rendered, tool-call turns collapse to a "used tools" note).
/// </summary>
public sealed class ResumedTranscriptTests
{
    private static string StripAnsi(string s) =>
        Regex.Replace(s, "\x1b\\[[0-9;]*m", "");

    [Fact]
    public void Fresh_session_with_no_history_prints_nothing()
    {
        var session = Session.NewEphemeral("glm-5.2:cloud");
        var sw = new StringWriter();

        Program.PrintResumedTranscript(session, sw, richConsole: null, markdownAnsi: null);

        sw.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Replays_user_and_assistant_turns_with_a_header_and_footer()
    {
        var session = Session.NewEphemeral("glm-5.2:cloud");
        session.AddSystem("system prompt that must NOT appear");
        session.AddUser("what is 2+2?");
        session.AddAssistant("It is **4**.");
        session.AddUser("thanks");
        session.AddAssistant("You're welcome.");

        var sw = new StringWriter();
        Program.PrintResumedTranscript(session, sw, richConsole: null, markdownAnsi: null);
        var plain = StripAnsi(sw.ToString());

        plain.Should().Contain("resumed conversation · 2 turns");
        plain.Should().Contain("> what is 2+2?");
        plain.Should().Contain("It is **4**.");        // markdownAnsi null → raw content
        plain.Should().Contain("> thanks");
        plain.Should().Contain("You're welcome.");
        plain.Should().Contain("end of history");
        plain.Should().NotContain("system prompt that must NOT appear");
    }

    [Fact]
    public void Assistant_tool_call_turn_collapses_to_a_used_tools_note()
    {
        var session = Session.NewEphemeral("glm-5.2:cloud");
        session.AddUser("read the file");
        session.AddAssistant(null, System.Collections.Immutable.ImmutableArray.Create(
            new Zdtllm.LiteLLM.ToolCall("id1", "Read", "{}"),
            new Zdtllm.LiteLLM.ToolCall("id2", "Grep", "{}")));

        var sw = new StringWriter();
        Program.PrintResumedTranscript(session, sw, richConsole: null, markdownAnsi: null);
        var plain = StripAnsi(sw.ToString());

        plain.Should().Contain("⚙ Read, Grep");
    }

    [Fact]
    public void MarkdownAnsi_delegate_is_used_to_render_assistant_text_when_supplied()
    {
        var session = Session.NewEphemeral("glm-5.2:cloud");
        session.AddUser("q");
        session.AddAssistant("# Heading");

        var sw = new StringWriter();
        Program.PrintResumedTranscript(session, sw, richConsole: null, markdownAnsi: md => $"RENDERED<{md}>");
        var plain = StripAnsi(sw.ToString());

        plain.Should().Contain("RENDERED<# Heading>");
    }
}
