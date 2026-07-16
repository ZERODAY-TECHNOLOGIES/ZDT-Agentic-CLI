using System.Text;
using System.Text.Json;

namespace Zdtllm.Tools;

/// <summary>
/// Lets the model offer the user a set of concrete choices and have them pick with the arrow
/// keys — the same interaction claude-cli exposes as <c>AskUserQuestion</c>. Registered ONLY in
/// interactive mode (a real TTY); in print mode / subagents the tool either isn't present or its
/// prompter is <see cref="UnavailablePrompter"/>, in which case it returns a clean error nudging
/// the model to decide on its own rather than blocking on input that will never arrive.
///
/// The tool is model-agnostic: it's advertised through the normal tool schema, so it works with
/// both native and XML tool-calling transports and with any LiteLLM-served model.
/// </summary>
public sealed class AskUserQuestionTool : ITool
{
    public const string ToolName = "AskUserQuestion";

    private readonly IInteractivePrompter _prompter;

    public AskUserQuestionTool(IInteractivePrompter prompter)
    {
        ArgumentNullException.ThrowIfNull(prompter);
        _prompter = prompter;
    }

    public ToolSchema Schema { get; } = new(
        Name: ToolName,
        Description:
            "Ask the user to choose between options, in interactive mode. Presents an arrow-key " +
            "selectable list and returns the choice(s). Use this when you are blocked on a decision " +
            "only the user can make — a genuine fork where their answer changes what you do next — " +
            "not for confirmations or questions with an obvious default. You may ask 1-4 questions " +
            "at once; each renders its own selectable list. For open-ended input, ask in plain prose " +
            "instead of using this tool.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                questions = new
                {
                    type = "array",
                    description = "1-4 questions to put to the user.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            question = new { type = "string", description = "The full question text." },
                            header = new { type = "string", description = "Very short label for the question (a few words)." },
                            multiSelect = new { type = "boolean", description = "Allow selecting more than one option (default false)." },
                            options = new
                            {
                                type = "array",
                                description = "The choices offered for this question (2-6 recommended).",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        label = new { type = "string", description = "Short option text the user selects." },
                                        description = new { type = "string", description = "Optional one-line explanation of the option." },
                                    },
                                    required = new[] { "label" },
                                },
                            },
                        },
                        required = new[] { "question", "options" },
                    },
                },
            },
            required = new[] { "questions" },
        }));

    /// <summary>Blocks on human input — must never run concurrently with another tool.</summary>
    public bool CanRunInParallel => false;

    /// <summary>Drives the interactive console; AgentLoop must not wrap it in a status spinner.</summary>
    public bool IsInteractive => true;

    /// <summary>A subagent has no human to ask — hand it a prompter that reports unavailable.</summary>
    public ITool CloneForSubagent() => new AskUserQuestionTool(UnavailablePrompter.Instance);

    public string? GetSpecifierForPermissions(JsonElement args) => null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!_prompter.IsAvailable)
            return ToolResult.Error(
                "AskUserQuestion is unavailable here (no interactive terminal — e.g. print mode or a " +
                "subagent). Decide yourself using your best judgement and proceed; do not ask again.");

        if (!args.TryGetProperty("questions", out var questionsEl) || questionsEl.ValueKind != JsonValueKind.Array)
            return ToolResult.Error("AskUserQuestion: missing or invalid 'questions' array.");

        var questions = ParseQuestions(questionsEl, out var parseError);
        if (parseError is not null) return ToolResult.Error(parseError);
        if (questions.Count == 0) return ToolResult.Error("AskUserQuestion: 'questions' was empty.");

        var sb = new StringBuilder();
        sb.AppendLine("[AskUserQuestion — user responses]");
        try
        {
            foreach (var q in questions)
            {
                var selected = await _prompter
                    .SelectAsync(q.Question, q.Header, q.Options, q.MultiSelect, allowFreeText: true, ct)
                    .ConfigureAwait(false);

                var label = string.IsNullOrWhiteSpace(q.Header) ? q.Question : q.Header;
                var answer = selected.Count == 0 ? "(no selection)" : string.Join(", ", selected);
                sb.Append("- ").Append(label).Append(": ").AppendLine(answer);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // user cancelled the turn — let it propagate to the loop's cancellation path
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"AskUserQuestion: prompting failed: {ex.Message}");
        }

        return ToolResult.Success(sb.ToString().TrimEnd());
    }

    private static List<ParsedQuestion> ParseQuestions(JsonElement questionsEl, out string? error)
    {
        error = null;
        var result = new List<ParsedQuestion>();
        var qIdx = 0;
        foreach (var qEl in questionsEl.EnumerateArray())
        {
            qIdx++;
            if (qEl.ValueKind != JsonValueKind.Object)
            {
                error = $"AskUserQuestion: question #{qIdx} is not an object.";
                return result;
            }

            var question = GetString(qEl, "question");
            if (string.IsNullOrWhiteSpace(question))
            {
                error = $"AskUserQuestion: question #{qIdx} is missing 'question'.";
                return result;
            }

            var header = GetString(qEl, "header");
            var multiSelect = qEl.TryGetProperty("multiSelect", out var ms)
                && ms.ValueKind is JsonValueKind.True or JsonValueKind.False && ms.GetBoolean();

            if (!qEl.TryGetProperty("options", out var optsEl) || optsEl.ValueKind != JsonValueKind.Array)
            {
                error = $"AskUserQuestion: question #{qIdx} is missing an 'options' array.";
                return result;
            }

            var options = new List<PromptChoice>();
            foreach (var optEl in optsEl.EnumerateArray())
            {
                if (optEl.ValueKind == JsonValueKind.String)
                {
                    // Tolerate a bare-string option, some models emit ["a","b"] instead of objects.
                    var s = optEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) options.Add(new PromptChoice(s!));
                    continue;
                }
                if (optEl.ValueKind != JsonValueKind.Object) continue;
                var lbl = GetString(optEl, "label");
                if (string.IsNullOrWhiteSpace(lbl)) continue;
                options.Add(new PromptChoice(lbl!, GetString(optEl, "description")));
            }

            if (options.Count == 0)
            {
                error = $"AskUserQuestion: question #{qIdx} has no valid options.";
                return result;
            }

            result.Add(new ParsedQuestion(question!, header, multiSelect, options));
        }
        return result;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed record ParsedQuestion(
        string Question, string? Header, bool MultiSelect, IReadOnlyList<PromptChoice> Options);
}
