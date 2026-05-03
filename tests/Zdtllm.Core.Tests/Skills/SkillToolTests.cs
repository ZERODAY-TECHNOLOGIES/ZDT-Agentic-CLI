using System.Text.Json;
using Zdtllm.Skills;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Skills;

public sealed class SkillToolTests
{
    private static SkillDefinition Skill(string name, string description = "Some skill", string basePath = "/x", string body = "BODY") =>
        new(name, description, basePath, body);

    private static async Task<ToolResult> InvokeAsync(SkillTool tool, string command)
    {
        var argsJson = JsonSerializer.Serialize(new { command });
        using var doc = JsonDocument.Parse(argsJson);
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);
    }

    [Fact]
    public async Task Returns_base_path_and_body_for_known_skill()
    {
        var tool = new SkillTool([Skill("hello", "Greets", "/skills/hello", "# Hello\nbody")]);

        var result = await InvokeAsync(tool, "hello");

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("SKILL: hello");
        result.Content.Should().Contain("BASE_DIR: /skills/hello");
        result.Content.Should().Contain("# Hello");
    }

    [Fact]
    public async Task Returns_error_for_unknown_skill_listing_available()
    {
        var tool = new SkillTool([Skill("alpha"), Skill("beta")]);

        var result = await InvokeAsync(tool, "gamma");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not found");
        result.Content.Should().Contain("alpha");
        result.Content.Should().Contain("beta");
    }

    [Fact]
    public async Task Empty_skills_list_lists_none_in_error()
    {
        var tool = new SkillTool([]);

        var result = await InvokeAsync(tool, "anything");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("(none)");
    }

    [Fact]
    public async Task Returns_error_when_command_missing()
    {
        var tool = new SkillTool([Skill("x")]);
        using var doc = JsonDocument.Parse("{}");

        var result = await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("command");
    }

    [Fact]
    public void Specifier_for_permissions_is_the_skill_name()
    {
        var tool = new SkillTool([Skill("x")]);
        using var doc = JsonDocument.Parse("""{"command":"my-skill"}""");

        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("my-skill");
    }
}
