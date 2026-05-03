using System.Text.Json;

namespace Zdtllm.Core.Sessions;

/// <summary>
/// Tracks the most recent session ID per CWD, keyed off canonical absolute paths.
/// Persisted as a small JSON map at <c>~/.zdtllm/recent.json</c> (override the path
/// for tests via the constructor).
/// </summary>
public sealed class RecentTracker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Dictionary<string, string> _byCwd;

    public RecentTracker(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _byCwd = LoadOrEmpty(path);
    }

    public static RecentTracker ForUserHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new RecentTracker(Path.Combine(home, ".zdtllm", "recent.json"));
    }

    public string? GetMostRecentForCwd(string cwd)
    {
        var key = Normalize(cwd);
        return _byCwd.TryGetValue(key, out var id) ? id : null;
    }

    public void Mark(string cwd, string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _byCwd[Normalize(cwd)] = sessionId;
        Persist();
    }

    private void Persist()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_byCwd, JsonOpts));
    }

    private static Dictionary<string, string> LoadOrEmpty(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text, JsonOpts)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Corrupted file — start fresh rather than crash.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string Normalize(string cwd)
    {
        ArgumentException.ThrowIfNullOrEmpty(cwd);
        var full = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Windows + macOS default filesystems are case-insensitive; lowercasing keys
        // means `/Users/x/proj` and `/users/x/Proj` both find the same recent session.
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? full.ToLowerInvariant()
            : full;
    }
}
