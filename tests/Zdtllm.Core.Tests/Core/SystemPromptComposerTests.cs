using Zdtllm.Core;
using Zdtllm.Skills;

namespace Zdtllm.Core.Tests.Core;

public sealed class SystemPromptComposerTests
{
    private const string Base = "You are zdt. Be concise.";

    [Fact]
    public void Just_base_text_is_returned_unchanged()
    {
        var result = SystemPromptComposer.Compose(Base);

        result.Should().Be("You are zdt. Be concise.");
    }

    [Fact]
    public void Append_text_follows_base_with_a_blank_line_separator()
    {
        var result = SystemPromptComposer.Compose(Base, appendText: "Extra rule.");

        result.Should().Contain(Base);
        result.Should().Contain("Extra rule.");
        result.IndexOf("Extra rule.").Should().BeGreaterThan(result.IndexOf(Base));
    }

    [Fact]
    public void Memory_file_block_is_labelled_and_placed_after_base()
    {
        var result = SystemPromptComposer.Compose(Base,
            memoryFile: "Project-specific instruction X.");

        result.Should().Contain("# Project memory (ZDTLLM.md)");
        result.Should().Contain("Project-specific instruction X.");
    }

    [Fact]
    public void Additional_directories_block_lists_each_directory()
    {
        var result = SystemPromptComposer.Compose(Base,
            additionalDirectories: ["../docs/", "/srv/data/"]);

        result.Should().Contain("# Additional accessible directories");
        result.Should().Contain("- ../docs/");
        result.Should().Contain("- /srv/data/");
    }

    [Fact]
    public void Skills_block_lists_each_name_and_description_with_invocation_hint()
    {
        var skills = new[]
        {
            new SkillDefinition("alpha", "First skill.", "/p/alpha", "BODY"),
            new SkillDefinition("beta", "Second skill.", "/p/beta", "BODY"),
        };

        var result = SystemPromptComposer.Compose(Base, skills: skills);

        result.Should().Contain("<available_skills>");
        result.Should().Contain("- alpha: First skill.");
        result.Should().Contain("- beta: Second skill.");
        result.Should().Contain("call the Skill tool");
        result.Should().EndWith("</available_skills>");
    }

    [Fact]
    public void Sections_appear_in_order_base_append_memory_dirs_skills()
    {
        var result = SystemPromptComposer.Compose(
            baseText: "[BASE]",
            appendText: "[APPEND]",
            memoryFile: "[MEMORY]",
            additionalDirectories: ["[DIR]"],
            skills: [new SkillDefinition("s1", "[SKILL-DESC]", "/p", "B")]);

        var iBase = result.IndexOf("[BASE]");
        var iAppend = result.IndexOf("[APPEND]");
        var iMemory = result.IndexOf("[MEMORY]");
        var iDir = result.IndexOf("[DIR]");
        var iSkill = result.IndexOf("[SKILL-DESC]");

        iBase.Should().BeLessThan(iAppend);
        iAppend.Should().BeLessThan(iMemory);
        iMemory.Should().BeLessThan(iDir);
        iDir.Should().BeLessThan(iSkill);
    }

    [Fact]
    public void Empty_skills_list_omits_the_block_entirely()
    {
        var result = SystemPromptComposer.Compose(Base, skills: []);

        result.Should().NotContain("<available_skills>");
    }

    [Fact]
    public void Whitespace_only_memory_file_is_treated_as_no_memory()
    {
        var result = SystemPromptComposer.Compose(Base, memoryFile: "   \n\n   ");

        result.Should().NotContain("Project memory");
    }

    [Fact]
    public void Empty_additional_directories_list_omits_the_block()
    {
        var result = SystemPromptComposer.Compose(Base, additionalDirectories: []);

        result.Should().NotContain("Additional accessible directories");
    }
}
