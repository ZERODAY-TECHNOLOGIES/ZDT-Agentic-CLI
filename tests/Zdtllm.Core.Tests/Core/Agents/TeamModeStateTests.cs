using Zdtllm.Core.Agents;

namespace Zdtllm.Core.Tests.Core.Agents;

public sealed class TeamModeStateTests
{
    [Fact]
    public void Starts_off_and_only_toggles_via_enter_end()
    {
        var team = new TeamModeState();
        team.InTeamMode.Should().BeFalse();

        team.Enter();
        team.InTeamMode.Should().BeTrue();

        team.End();
        team.InTeamMode.Should().BeFalse();
    }

    [Fact]
    public void Blocks_exactly_the_mutating_tools()
    {
        TeamModeState.BlockedTools.Should().BeEquivalentTo(new[] { "Write", "Edit", "NotebookEdit", "Bash" });
        TeamModeState.BlockedTools.Should().NotContain("Read");
        TeamModeState.BlockedTools.Should().NotContain("Agent");
    }

    [Fact]
    public void Reminder_names_the_role_the_project_agents_and_the_builtins()
    {
        var roster = new[]
        {
            new AgentDefinition("db-migrator", "runs SQL migrations", null, "p", null),
            new AgentDefinition("api-builder", "builds endpoints", null, "p", null),
        };

        var reminder = TeamModeState.BuildReminder(roster);

        reminder.Should().Contain("TEAM MODE ON");
        reminder.Should().Contain("db-migrator");
        reminder.Should().Contain("runs SQL migrations");
        reminder.Should().Contain("api-builder");
        reminder.Should().Contain("general-purpose");
        reminder.Should().Contain("Agent tool");
    }

    [Fact]
    public void Reminder_drops_a_builtin_that_a_project_agent_has_shadowed()
    {
        var roster = new[]
        {
            new AgentDefinition("explore", "our custom explorer", null, "p", null),
        };

        var reminder = TeamModeState.BuildReminder(roster);

        // The project 'explore' is listed with its own blurb; the builtin explore line is gone.
        reminder.Should().Contain("our custom explorer");
        reminder.Should().NotContain("read-only research");
        // Unshadowed builtins are still advertised.
        reminder.Should().Contain("general-purpose (all tools)");
    }

    [Fact]
    public void Reminder_is_fine_with_an_empty_roster()
    {
        var reminder = TeamModeState.BuildReminder(Array.Empty<AgentDefinition>());
        reminder.Should().Contain("TEAM MODE ON");
        reminder.Should().NotContain("Project subagents available");
    }

    [Fact]
    public void Blocked_message_tells_the_model_to_delegate()
    {
        var msg = TeamModeState.BlockedMessage("Write");
        msg.Should().Contain("team mode is ON");
        msg.Should().Contain("Write");
        msg.Should().Contain("Agent tool");
    }
}
