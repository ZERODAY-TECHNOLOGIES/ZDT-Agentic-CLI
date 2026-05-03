using Zdtllm.Skills;

namespace Zdtllm.Core.Tests.Skills;

public sealed class SkillsLoaderTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _userSkillsRoot;
    private readonly string _projectDir;

    public SkillsLoaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "zdt-skills-" + Guid.NewGuid().ToString("N"));
        _userSkillsRoot = Path.Combine(_tempRoot, "user-skills");
        _projectDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(_userSkillsRoot);
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private void WriteUserSkill(string name, string frontmatter, string body)
    {
        var dir = Path.Combine(_userSkillsRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\n{frontmatter}\n---\n{body}");
    }

    private void WriteProjectSkill(string name, string frontmatter, string body)
    {
        var dir = Path.Combine(_projectDir, ".zdtllm", "skills", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\n{frontmatter}\n---\n{body}");
    }

    private IReadOnlyList<SkillDefinition> Discover() =>
        new SkillsLoader().Discover(
            _projectDir,
            new SkillsLoaderOptions { UserSkillsRoot = _userSkillsRoot });

    [Fact]
    public void Discovers_user_level_skill()
    {
        WriteUserSkill("hello",
            "name: hello\ndescription: Greets the world.",
            "# Hello\nThis is the body.");

        var skills = Discover();

        skills.Should().ContainSingle();
        skills[0].Name.Should().Be("hello");
        skills[0].Description.Should().Be("Greets the world.");
        skills[0].Body.Should().StartWith("# Hello");
        skills[0].BasePath.Should().EndWith("hello");
    }

    [Fact]
    public void Discovers_project_level_skill()
    {
        WriteProjectSkill("scoped",
            "name: scoped\ndescription: Project-only skill.",
            "Body.");

        var skills = Discover();

        skills.Should().ContainSingle();
        skills[0].Name.Should().Be("scoped");
        skills[0].BasePath.Should().Contain(".zdtllm");
    }

    [Fact]
    public void Project_skill_overrides_user_skill_with_same_name()
    {
        WriteUserSkill("shared",
            "name: shared\ndescription: User version.",
            "User body.");
        WriteProjectSkill("shared",
            "name: shared\ndescription: Project version.",
            "Project body.");

        var skills = Discover();

        skills.Should().ContainSingle();
        skills[0].Description.Should().Be("Project version.");
        skills[0].Body.Should().Contain("Project body");
    }

    [Fact]
    public void Skips_skill_when_directory_name_does_not_match_frontmatter_name()
    {
        WriteUserSkill("real-name",
            "name: different-name\ndescription: Mismatched.",
            "body");

        Discover().Should().BeEmpty();
    }

    [Fact]
    public void Skips_skill_with_invalid_name_format()
    {
        WriteUserSkill("Bad_Name", // capitals + underscore: invalid per spec
            "name: Bad_Name\ndescription: Invalid format.",
            "body");

        Discover().Should().BeEmpty();
    }

    [Fact]
    public void Skips_skill_with_overlong_description()
    {
        WriteUserSkill("long",
            $"name: long\ndescription: \"{new string('x', 1025)}\"",
            "body");

        Discover().Should().BeEmpty();
    }

    [Fact]
    public void Skips_skill_with_missing_frontmatter()
    {
        var dir = Path.Combine(_userSkillsRoot, "no-meta");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "Just a body, no frontmatter.");

        Discover().Should().BeEmpty();
    }

    [Fact]
    public void Skips_skill_with_missing_required_field()
    {
        WriteUserSkill("no-desc",
            "name: no-desc",
            "body");

        Discover().Should().BeEmpty();
    }

    [Fact]
    public void Body_excludes_frontmatter_and_is_trimmed()
    {
        WriteUserSkill("trimmed",
            "name: trimmed\ndescription: Verifies body trimming.",
            "\n\n# Heading\n\nContent.\n\n");

        var skills = Discover();

        skills.Should().ContainSingle();
        skills[0].Body.Should().StartWith("# Heading");
        skills[0].Body.Should().NotStartWith("---");
        skills[0].Body.TrimEnd().Should().EndWith("Content.");
    }

    [Fact]
    public void Returns_skills_sorted_by_name()
    {
        WriteUserSkill("zeta", "name: zeta\ndescription: Last alphabetically.", "z");
        WriteUserSkill("alpha", "name: alpha\ndescription: First alphabetically.", "a");
        WriteUserSkill("middle", "name: middle\ndescription: In the middle.", "m");

        var names = Discover().Select(s => s.Name).ToArray();

        names.Should().Equal("alpha", "middle", "zeta");
    }

    [Fact]
    public void One_malformed_skill_does_not_break_discovery_of_others()
    {
        WriteUserSkill("good",
            "name: good\ndescription: Valid.",
            "body");

        // Malformed: invalid YAML
        var dir = Path.Combine(_userSkillsRoot, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\n{not yaml at all\n---\nbody");

        Discover().Should().ContainSingle().Which.Name.Should().Be("good");
    }
}
