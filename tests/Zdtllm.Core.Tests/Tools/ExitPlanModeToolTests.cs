using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class ExitPlanModeToolTests
{
    private sealed class FakePrompter : IInteractivePrompter
    {
        private readonly string _choice;
        public bool IsAvailable { get; }
        public FakePrompter(bool available, string choice) { IsAvailable = available; _choice = choice; }
        public Task<IReadOnlyList<string>> SelectAsync(
            string question, string? header, IReadOnlyList<PromptChoice> options,
            bool multiSelect, bool allowFreeText, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { _choice });
    }

    private static async Task<ToolResult> RunAsync(ExitPlanModeTool tool, string plan = "1. do a thing")
    {
        var json = JsonSerializer.Serialize(new { plan });
        using var doc = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);
    }

    [Fact]
    public async Task Approving_the_plan_turns_plan_mode_off()
    {
        var plan = new PlanModeState(active: true);
        var tool = new ExitPlanModeTool(plan, new FakePrompter(true, "Approve — proceed with changes"));

        var result = await RunAsync(tool);

        result.IsError.Should().BeFalse();
        plan.InPlanMode.Should().BeFalse();
        result.Content.Should().Contain("plan approved");
    }

    [Fact]
    public async Task Declining_keeps_plan_mode_on()
    {
        var plan = new PlanModeState(active: true);
        var tool = new ExitPlanModeTool(plan, new FakePrompter(true, "Keep planning"));

        var result = await RunAsync(tool);

        plan.InPlanMode.Should().BeTrue();
        result.Content.Should().Contain("keep planning");
    }

    [Fact]
    public async Task Not_in_plan_mode_is_an_error()
    {
        var plan = new PlanModeState(active: false);
        var tool = new ExitPlanModeTool(plan, new FakePrompter(true, "Approve — proceed with changes"));

        var result = await RunAsync(tool);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not currently in plan mode");
    }

    [Fact]
    public async Task Unavailable_prompter_returns_error()
    {
        var plan = new PlanModeState(active: true);
        var tool = new ExitPlanModeTool(plan, new FakePrompter(available: false, "Approve"));

        var result = await RunAsync(tool);

        result.IsError.Should().BeTrue();
        plan.InPlanMode.Should().BeTrue(); // not approved
    }

    [Fact]
    public async Task Subagent_clone_cannot_approve()
    {
        var plan = new PlanModeState(active: true);
        var clone = new ExitPlanModeTool(plan, new FakePrompter(true, "Approve")).CloneForSubagent();

        var json = JsonSerializer.Serialize(new { plan = "x" });
        using var doc = JsonDocument.Parse(json);
        var result = await clone.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);

        result.IsError.Should().BeTrue();
        plan.InPlanMode.Should().BeTrue();
    }
}
