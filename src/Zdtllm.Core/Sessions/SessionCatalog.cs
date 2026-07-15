using System.Text.Json;

namespace Zdtllm.Core.Sessions;

/// <summary>
/// A one-line summary of a persisted session, built for the interactive resume picker.
/// The <see cref="Title"/> is the first genuine user prompt (used as a human-readable
/// label), <see cref="LastModified"/> drives most-recent-first ordering, and the token /
/// turn counts give the picker something quantitative to show. This is intentionally a
/// lightweight projection — it does NOT replay the full session into a <see cref="Session"/>.
/// </summary>
public sealed record SessionSummary(
    string Id,
    string Model,
    string? Name,
    ToolCallingMode Mode,
    string? Title,
    int UserTurns,
    int AssistantTurns,
    DateTimeOffset Created,
    DateTimeOffset LastModified);

/// <summary>
/// Reads lightweight summaries of every session JSONL file in a sessions directory, so
/// callers (the <c>--resume</c> picker) can present a most-recent-first list without
/// fully rebuilding each conversation. Malformed / empty files are skipped rather than
/// throwing — a half-written tail from a crashed run shouldn't hide the rest of the list.
/// </summary>
public sealed class SessionCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _sessionsDir;

    public SessionCatalog(string sessionsDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionsDir);
        _sessionsDir = sessionsDir;
    }

    /// <summary>
    /// List session summaries, newest-first by file modification time. Pass a positive
    /// <paramref name="limit"/> to cap how many files are parsed (the picker only shows a
    /// handful, and parsing every historical JSONL would be wasteful). A limit of 0 means
    /// "all". Files whose meta event can't be read are omitted.
    /// </summary>
    public IReadOnlyList<SessionSummary> List(int limit = 0)
    {
        if (!Directory.Exists(_sessionsDir)) return Array.Empty<SessionSummary>();

        // Order by mtime first, THEN parse only the top slice — cheap even with hundreds of
        // historical sessions on disk.
        var files = new DirectoryInfo(_sessionsDir)
            .EnumerateFiles("*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .AsEnumerable();
        if (limit > 0) files = files.Take(limit);

        var summaries = new List<SessionSummary>();
        foreach (var file in files)
        {
            var summary = TryReadSummary(file);
            if (summary is not null) summaries.Add(summary);
        }
        return summaries;
    }

    private SessionSummary? TryReadSummary(FileInfo file)
    {
        var id = Path.GetFileNameWithoutExtension(file.Name);

        string? model = null;
        string? name = null;
        var mode = ToolCallingMode.Native;
        DateTimeOffset? created = null;
        string? title = null;
        var userTurns = 0;
        var assistantTurns = 0;
        // In XML mode, tool results are appended as synthetic user events, so counting raw
        // user events overcounts real turns. We treat the FIRST user event as the title and
        // count assistant events (which are 1:1 with model turns) as the more honest "turns"
        // signal; userTurns is still surfaced but kept approximate.

        try
        {
            using var reader = new StreamReader(
                File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                SessionEvent? ev;
                try { ev = JsonSerializer.Deserialize<SessionEvent>(line, JsonOpts); }
                catch (JsonException) { continue; }

                switch (ev)
                {
                    case MetaEvent meta:
                        model ??= meta.Model;
                        name ??= meta.Name;
                        mode = meta.Mode;
                        created ??= meta.Timestamp;
                        break;
                    case UserEvent u:
                        userTurns++;
                        title ??= Summarize(u.Content);
                        break;
                    case AssistantEvent:
                        assistantTurns++;
                        break;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }

        if (model is null) return null; // no meta event → not a resumable session

        return new SessionSummary(
            Id: id,
            Model: model,
            Name: name,
            Mode: mode,
            Title: title,
            UserTurns: userTurns,
            AssistantTurns: assistantTurns,
            Created: created ?? file.CreationTimeUtc,
            LastModified: file.LastWriteTimeUtc);
    }

    /// <summary>Collapse a user message to a single trimmed line for use as a picker label.</summary>
    private static string Summarize(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "(empty)";
        var firstLine = content.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? content.Trim();
        return firstLine;
    }
}
