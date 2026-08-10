using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zdtllm.Core.Sessions;

/// <summary>
/// Append-only writer + replay reader for one session's JSONL file at
/// <c>{sessionsDir}/{sessionId}.jsonl</c>. One <see cref="SessionEvent"/> per line.
/// Malformed lines on read are skipped (so a partial trailing write doesn't poison resume).
/// </summary>
public sealed class SessionStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private StreamWriter? _writer;
    private bool _disposed;

    public string SessionId { get; }
    public string Path => _path;

    private SessionStore(string path, string sessionId)
    {
        _path = path;
        SessionId = sessionId;
    }

    public static SessionStore Create(string sessionsDir, string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionsDir);
        sessionId ??= Guid.NewGuid().ToString();
        Directory.CreateDirectory(sessionsDir);
        var path = System.IO.Path.Combine(sessionsDir, $"{sessionId}.jsonl");
        return new SessionStore(path, sessionId);
    }

    public static SessionStore OpenForResume(string sessionsDir, string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionsDir);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var path = System.IO.Path.Combine(sessionsDir, $"{sessionId}.jsonl");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Session '{sessionId}' not found in '{sessionsDir}'.", path);
        return new SessionStore(path, sessionId);
    }

    public void Append(SessionEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_writer is null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            _writer = new StreamWriter(File.Open(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = false,
            };
        }

        var json = JsonSerializer.Serialize<SessionEvent>(ev, JsonOpts);
        _writer.WriteLine(json);
        _writer.Flush();
    }

    public IEnumerable<SessionEvent> ReadAll()
    {
        if (!File.Exists(_path)) yield break;
        using var reader = new StreamReader(File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            SessionEvent? ev;
            try
            {
                ev = JsonSerializer.Deserialize<SessionEvent>(line, JsonOpts);
            }
            catch (JsonException)
            {
                // Skip malformed lines (e.g. half-written tail of a previous run).
                continue;
            }
            if (ev is not null) yield return ev;
        }
    }

    /// <summary>
    /// Close the writer and delete the on-disk JSONL. Used by incognito mode to erase everything written
    /// so far while the conversation keeps running in memory. Best-effort: a locked or already-gone file
    /// is ignored. The store is spent afterwards (further <see cref="Append"/> throws; Dispose is a no-op).
    /// </summary>
    public void DeleteFile()
    {
        try { _writer?.Flush(); } catch { /* best effort */ }
        try { _writer?.Dispose(); } catch { /* best effort */ }
        _writer = null;
        _disposed = true;
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort — the file may be locked */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _writer?.Flush(); } catch { /* best effort */ }
        _writer?.Dispose();
        _writer = null;
    }
}
