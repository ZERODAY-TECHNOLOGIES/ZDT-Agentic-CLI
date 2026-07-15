using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class AskUserQuestionToolTests
{
    /// <summary>Records what it was asked and returns a scripted set of selected labels.</summary>
    private sealed class FakePrompter : IInteractivePrompter
    {
        private readonly IReadOnlyList<string> _answer;
        public bool IsAvailable { get; }
        public List<(string Question, bool MultiSelect, int OptionCount)> Calls { get; } = new();

        public FakePrompter(bool available, params string[] answer)
        {
            IsAvailable = available;
            _answer = answer;
        }

        public Task<IReadOnlyList<string>> SelectAsync(
            string question, string? header, IReadOnlyList<PromptChoice> options,
            bool multiSelect, CancellationToken ct)
        {
            Calls.Add((question, multiSelect, options.Count));
            return Task.FromResult(_answer);
        }
    }

    private static async Task<ToolResult> AskAsync(AskUserQuestionTool tool, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);
    }

    [Fact]
    public async Task Single_select_returns_chosen_label_to_the_model()
    {
        var prompter = new FakePrompter(available: true, "Postgres");
        var tool = new AskUserQuestionTool(prompter);

        var result = await AskAsync(tool, new
        {
            questions = new[]
            {
                new
                {
                    question = "Which database?",
                    header = "DB",
                    options = new[]
                    {
                        new { label = "Postgres", description = "relational" },
                        new { label = "Mongo", description = "document" },
                    },
                },
            },
        });

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("DB: Postgres");
        prompter.Calls.Should().ContainSingle();
        prompter.Calls[0].MultiSelect.Should().BeFalse();
        prompter.Calls[0].OptionCount.Should().Be(2);
    }

    [Fact]
    public async Task Multi_select_joins_all_chosen_labels()
    {
        var prompter = new FakePrompter(available: true, "Read", "Write");
        var tool = new AskUserQuestionTool(prompter);

        var result = await AskAsync(tool, new
        {
            questions = new[]
            {
                new
                {
                    question = "Which permissions?",
                    header = "Perms",
                    multiSelect = true,
                    options = new[]
                    {
                        new { label = "Read" },
                        new { label = "Write" },
                        new { label = "Bash" },
                    },
                },
            },
        });

        result.Content.Should().Contain("Perms: Read, Write");
        prompter.Calls[0].MultiSelect.Should().BeTrue();
    }

    [Fact]
    public async Task Unavailable_prompter_returns_error_instead_of_blocking()
    {
        var tool = new AskUserQuestionTool(new FakePrompter(available: false));

        var result = await AskAsync(tool, new
        {
            questions = new[]
            {
                new { question = "x?", options = new[] { new { label = "a" } } },
            },
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("unavailable");
    }

    [Fact]
    public async Task Clone_for_subagent_is_never_available()
    {
        var tool = new AskUserQuestionTool(new FakePrompter(available: true, "a"));
        var clone = tool.CloneForSubagent();

        var json = JsonSerializer.Serialize(new
        {
            questions = new[] { new { question = "x?", options = new[] { new { label = "a" } } } },
        });
        using var doc = JsonDocument.Parse(json);
        var result = await clone.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("unavailable");
    }

    [Fact]
    public async Task Missing_questions_array_is_an_error()
    {
        var tool = new AskUserQuestionTool(new FakePrompter(available: true, "a"));
        var result = await AskAsync(tool, new { nope = 1 });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("questions");
    }

    [Fact]
    public async Task Question_without_valid_options_is_an_error()
    {
        var tool = new AskUserQuestionTool(new FakePrompter(available: true, "a"));
        var result = await AskAsync(tool, new
        {
            questions = new[] { new { question = "x?", options = Array.Empty<object>() } },
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("options");
    }

    [Fact]
    public async Task Bare_string_options_are_tolerated()
    {
        var prompter = new FakePrompter(available: true, "yes");
        var tool = new AskUserQuestionTool(prompter);

        var result = await AskAsync(tool, new
        {
            questions = new[] { new { question = "Proceed?", options = new[] { "yes", "no" } } },
        });

        result.IsError.Should().BeFalse();
        prompter.Calls[0].OptionCount.Should().Be(2);
    }

    [Fact]
    public void Tool_is_interactive_and_not_parallelizable()
    {
        var tool = new AskUserQuestionTool(new FakePrompter(available: true));
        tool.IsInteractive.Should().BeTrue();
        tool.CanRunInParallel.Should().BeFalse();
    }
}
