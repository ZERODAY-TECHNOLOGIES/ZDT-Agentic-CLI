using Zdtllm.Core.Agents;

namespace Zdtllm.Core.Tests.Core.Agents;

public sealed class AgentDefinitionLoaderTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _projectAgents;
    private readonly string _userAgents;

    public AgentDefinitionLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zdt-agents-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_root, "project");
        _projectAgents = Path.Combine(_cwd, ".zdtllm", "agents");
        _userAgents = Path.Combine(_root, "user", "agents");
        Directory.CreateDirectory(_projectAgents);
        Directory.CreateDirectory(_userAgents);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private void WriteProject(string file, string content) =>
        File.WriteAllText(Path.Combine(_projectAgents, file), content);

    private void WriteUser(string file, string content) =>
        File.WriteAllText(Path.Combine(_userAgents, file), content);

    private IReadOnlyList<AgentDefinition> Discover() =>
        new AgentDefinitionLoader().Discover(_cwd, userRootOverride: _userAgents);

    [Fact]
    public void Parses_frontmatter_tools_model_and_body()
    {
        WriteProject("db-migrator.md",
            "---\nname: db-migrator\ndescription: Writes and runs SQL migrations\ntools: Read, Edit, Bash\nmodel: light\n---\n" +
            "You are the migrator. Be careful.");

        var def = Discover().Single();

        def.Name.Should().Be("db-migrator");
        def.Description.Should().Be("Writes and runs SQL migrations");
        def.AllowedTools.Should().BeEquivalentTo(new[] { "Read", "Edit", "Bash" });
        def.Model.Should().Be("light");
        def.SystemPrompt.Should().Be("You are the migrator. Be careful.");
    }

    [Fact]
    public void Tools_all_and_inherit_model_normalise_to_null()
    {
        WriteProject("worker.md",
            "---\ndescription: does anything\ntools: all\nmodel: inherit\n---\nbody");

        var def = Discover().Single();

        def.Name.Should().Be("worker"); // falls back to filename
        def.AllowedTools.Should().BeNull();  // "all" → no restriction
        def.Model.Should().BeNull();          // "inherit" → parent model
    }

    [Fact]
    public void Missing_tools_defaults_to_all_and_missing_body_gets_a_default_prompt()
    {
        WriteProject("bare.md", "---\ndescription: minimal\n---\n");

        var def = Discover().Single();

        def.AllowedTools.Should().BeNull();
        def.SystemPrompt.Should().Contain("bare");   // generated default mentions the name
        def.SystemPrompt.Should().NotBeEmpty();
    }

    [Fact]
    public void No_frontmatter_uses_filename_and_whole_file_as_prompt()
    {
        WriteProject("plain.md", "Just a system prompt, no frontmatter.");

        var def = Discover().Single();

        def.Name.Should().Be("plain");
        def.SystemPrompt.Should().Be("Just a system prompt, no frontmatter.");
        def.AllowedTools.Should().BeNull();
    }

    [Fact]
    public void Project_agent_overrides_a_user_agent_of_the_same_name()
    {
        WriteUser("shared.md", "---\ndescription: from user\n---\nuser body");
        WriteProject("shared.md", "---\ndescription: from project\n---\nproject body");

        var def = Discover().Single();

        def.Description.Should().Be("from project");
        def.SystemPrompt.Should().Be("project body");
    }

    [Fact]
    public void Invalid_slug_filename_is_skipped()
    {
        WriteProject("Not A Slug.md", "---\ndescription: bad name\n---\nbody");

        Discover().Should().BeEmpty();
    }

    [Fact]
    public void Round_trips_through_ToMarkdown()
    {
        var original = new AgentDefinition(
            "api-builder", "Builds REST endpoints",
            new HashSet<string>(StringComparer.Ordinal) { "Read", "Write", "Edit" },
            "You build APIs.", "medium");

        WriteProject("api-builder.md", AgentDefinitionLoader.ToMarkdown(original));
        var loaded = Discover().Single();

        loaded.Name.Should().Be(original.Name);
        loaded.Description.Should().Be(original.Description);
        loaded.AllowedTools.Should().BeEquivalentTo(original.AllowedTools);
        loaded.SystemPrompt.Should().Be(original.SystemPrompt);
        loaded.Model.Should().Be(original.Model);
    }

    [Fact]
    public void ParseTools_treats_all_anywhere_in_the_list_as_no_restriction()
    {
        AgentDefinitionLoader.ParseTools("Read, all, Bash").Should().BeNull();
        AgentDefinitionLoader.ParseTools("  ").Should().BeNull();
        AgentDefinitionLoader.ParseTools("Read Edit").Should().BeEquivalentTo(new[] { "Read", "Edit" });
    }
}
