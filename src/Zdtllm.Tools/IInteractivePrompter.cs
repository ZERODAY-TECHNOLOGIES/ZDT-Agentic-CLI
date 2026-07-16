namespace Zdtllm.Tools;

/// <summary>One selectable option presented to the user by <see cref="IInteractivePrompter"/>.</summary>
public sealed record PromptChoice(string Label, string? Description = null);

/// <summary>
/// Abstraction over "ask the human to choose from a list, with arrow-key navigation." The
/// concrete implementation lives in the CLI (it drives the real console); the tool layer
/// depends only on this interface so it stays free of Spectre / console specifics and remains
/// unit-testable. When no interactive terminal is available (print mode, a subagent, redirected
/// stdin) <see cref="IsAvailable"/> is false and callers must NOT prompt.
/// </summary>
public interface IInteractivePrompter
{
    /// <summary>True when a real interactive terminal is attached and prompting is possible.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Present <paramref name="options"/> for <paramref name="question"/> and block until the
    /// user chooses. Returns the selected labels in display order — exactly one for a
    /// single-select prompt, zero-or-more when <paramref name="multiSelect"/> is true. When
    /// <paramref name="allowFreeText"/> is set, an extra "type your own answer" option is offered
    /// and, if picked, the user's typed text is returned in place of a canned label.
    /// </summary>
    Task<IReadOnlyList<string>> SelectAsync(
        string question,
        string? header,
        IReadOnlyList<PromptChoice> options,
        bool multiSelect,
        bool allowFreeText,
        CancellationToken ct);
}

/// <summary>
/// Prompter used where no human is reachable (subagents, non-interactive runs). Every call
/// throws so <see cref="AskUserQuestionTool"/> can turn it into a clean tool error telling the
/// model to decide for itself instead of hanging forever waiting on input that will never come.
/// </summary>
public sealed class UnavailablePrompter : IInteractivePrompter
{
    public static readonly UnavailablePrompter Instance = new();

    public bool IsAvailable => false;

    public Task<IReadOnlyList<string>> SelectAsync(
        string question, string? header, IReadOnlyList<PromptChoice> options,
        bool multiSelect, bool allowFreeText, CancellationToken ct) =>
        throw new InvalidOperationException("No interactive terminal is available for prompting.");
}
