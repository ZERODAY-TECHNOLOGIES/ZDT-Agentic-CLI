using Zdtllm.Core.Agents;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core.Agents;

public sealed class AgentWizardTests : IDisposable
{
    private readonly string _cwd;

    public AgentWizardTests()
    {
        _cwd = Path.Combine(Path.GetTempPath(), "zdt-wizard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cwd, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Returns queued answers in order, one per SelectAsync call.</summary>
    private sealed class QueuePrompter : IInteractivePrompter
    {
        private readonly Queue<IReadOnlyList<string>> _answers;
        public QueuePrompter(bool available, params IReadOnlyList<string>[] answers)
        {
            IsAvailable = available;
            _answers = new Queue<IReadOnlyList<string>>(answers);
        }
        public bool IsAvailable { get; }
        public Task<IReadOnlyList<string>> SelectAsync(
            string question, string? header, IReadOnlyList<PromptChoice> options,
            bool multiSelect, bool allowFreeText, CancellationToken ct) =>
            Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : Array.Empty<string>());
    }

    [Fact]
    public async Task Defines_an_agent_registers_it_and_writes_the_file()
    {
        var registry = new TeamAgentRegistry();
        var prompter = new QueuePrompter(available: true,
            new[] { "db migrator" },                    // name (gets slugified)
            new[] { "Writes and runs SQL migrations" },  // description
            new[] { "Read", "Edit", "Bash" },            // tools (multi-select)
            new[] { "light" },                           // model
            new[] { AgentWizard.GeneratePromptLabel });  // system prompt → generated

        var wizard = new AgentWizard(prompter, registry, _cwd, new StringWriter());
        var def = await wizard.RunAsync();

        def.Should().NotBeNull();
        def!.Name.Should().Be("db-migrator");
        def.Description.Should().Be("Writes and runs SQL migrations");
        def.AllowedTools.Should().BeEquivalentTo(new[] { "Read", "Edit", "Bash" });
        def.Model.Should().Be("light");
        def.SystemPrompt.Should().Contain("db-migrator");

        registry.Contains("db-migrator").Should().BeTrue();

        var path = Path.Combine(_cwd, ".zdtllm", "agents", "db-migrator.md");
        File.Exists(path).Should().BeTrue();

        // The written file round-trips back through the loader with the same fields.
        var reloaded = new AgentDefinitionLoader().Discover(_cwd, userRootOverride: Path.Combine(_cwd, "nope"))
            .Single();
        reloaded.Name.Should().Be("db-migrator");
        reloaded.AllowedTools.Should().BeEquivalentTo(new[] { "Read", "Edit", "Bash" });
        reloaded.Model.Should().Be("light");
    }

    [Fact]
    public async Task All_tools_choice_yields_an_unrestricted_agent()
    {
        var registry = new TeamAgentRegistry();
        var prompter = new QueuePrompter(available: true,
            new[] { "worker" },
            new[] { "does anything" },
            new[] { AgentWizard.AllToolsLabel },          // tools → all
            new[] { AgentWizard.InheritModelLabel },      // model → inherit (null)
            new[] { AgentWizard.GeneratePromptLabel });

        var def = await new AgentWizard(prompter, registry, _cwd, new StringWriter()).RunAsync();

        def!.AllowedTools.Should().BeNull();
        def.Model.Should().BeNull();
    }

    [Fact]
    public async Task A_custom_system_prompt_is_used_when_provided()
    {
        var registry = new TeamAgentRegistry();
        var prompter = new QueuePrompter(available: true,
            new[] { "reviewer" },
            new[] { "reviews code" },
            new[] { "Read", "Grep" },
            new[] { AgentWizard.InheritModelLabel },
            new[] { AgentWizard.WritePromptLabel },       // choose to write my own…
            new[] { "ONLY review, never edit." });        // …the custom prompt

        var def = await new AgentWizard(prompter, registry, _cwd, new StringWriter()).RunAsync();

        def!.SystemPrompt.Should().Be("ONLY review, never edit.");
    }

    [Fact]
    public async Task Unavailable_prompter_defines_nothing()
    {
        var registry = new TeamAgentRegistry();
        var def = await new AgentWizard(new QueuePrompter(available: false), registry, _cwd, new StringWriter())
            .RunAsync();

        def.Should().BeNull();
        registry.Count.Should().Be(0);
    }

    [Fact]
    public async Task Empty_name_aborts_the_definition()
    {
        var registry = new TeamAgentRegistry();
        var prompter = new QueuePrompter(available: true, Array.Empty<string>());

        var def = await new AgentWizard(prompter, registry, _cwd, new StringWriter()).RunAsync();

        def.Should().BeNull();
        registry.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("db migrator", "db-migrator")]
    [InlineData("DB_Migrator", "db-migrator")]
    [InlineData("  API  Builder  ", "api-builder")]
    [InlineData("weird!!name", "weirdname")]
    public void Slugify_produces_valid_slugs(string input, string expected) =>
        AgentWizard.Slugify(input).Should().Be(expected);

    [Fact]
    public void Generated_prompt_mentions_the_role_and_the_tools()
    {
        var prompt = AgentWizard.GenerateSystemPrompt(
            "db-migrator", "runs SQL migrations",
            new HashSet<string>(StringComparer.Ordinal) { "Read", "Bash" });

        prompt.Should().Contain("db-migrator");
        prompt.Should().Contain("runs SQL migrations");
        prompt.Should().Contain("Read");
        prompt.Should().Contain("Bash");
    }
}
