using System.Text;

namespace Zdtllm.Cli.Input;

/// <summary>
/// Pure text helpers for the interactive line editor: cleaning up what a paste / drag-and-drop
/// delivers before it reaches <see cref="LineEditorState"/>. Kept free of any <c>Console</c> use
/// so they're unit-testable.
/// </summary>
internal static class InputText
{
    private const char Esc = '\x1b';

    /// <summary>
    /// Remove bracketed-paste control sequences (<c>ESC[200~</c> / <c>ESC[201~</c>) and any stray
    /// ESC bytes from a reconstructed key burst. When bracketed paste is enabled the terminal wraps
    /// pasted content in these markers; read key-by-key they arrive as literal characters we must
    /// not treat as input. Terminals that don't support the mode simply never emit them, so this is
    /// a no-op there.
    /// </summary>
    public static string StripBracketedPasteMarkers(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Replace($"{Esc}[200~", string.Empty).Replace($"{Esc}[201~", string.Empty);
        // Also drop bare markers in case the ESC was translated away, plus any lone ESC bytes.
        s = s.Replace("[200~", string.Empty).Replace("[201~", string.Empty);
        if (s.IndexOf(Esc) >= 0) s = s.Replace(Esc.ToString(), string.Empty);
        return s;
    }

    /// <summary>
    /// Normalise the text a terminal inserts when a file is dragged & dropped onto it: usually the
    /// path, often single- or double-quoted, sometimes as a <c>file://</c> URI, sometimes (on
    /// Unix) with backslash-escaped spaces. Returns a clean absolute-ish path string. If the input
    /// doesn't look like a single dropped path it's returned unchanged.
    /// </summary>
    public static string NormalizeDroppedPath(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var t = s.Trim();

        // Strip one layer of matching surrounding quotes.
        if (t.Length >= 2 &&
            ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\'')))
        {
            t = t[1..^1];
        }

        // file:// URI → local path.
        if (t.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                try { return uri.LocalPath; }
                catch { /* fall through to raw */ }
            }
        }

        // Unix drag&drop escapes spaces as "\ ". Only unescape when EVERY space in the string is a
        // backslash-escaped one (i.e. removing the "\ " pairs leaves no bare spaces) — that marks a
        // dropped path rather than normal prose, which we must not mangle.
        if (t.Contains("\\ ") && !t.Replace("\\ ", string.Empty).Contains(' '))
            t = t.Replace("\\ ", " ");

        return t;
    }

    /// <summary>
    /// True when <paramref name="s"/> is a single line that resolves to an existing file or
    /// directory — used to show a "dropped file" confirmation. Best-effort; never throws.
    /// </summary>
    public static bool LooksLikeExistingPath(string s, out string normalized)
    {
        normalized = NormalizeDroppedPath(s);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.IndexOf('\n') >= 0) return false;
        try { return File.Exists(normalized) || Directory.Exists(normalized); }
        catch { return false; }
    }

    private static readonly IReadOnlyDictionary<string, string> ImageMimeByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
        };

    /// <summary>Max image size we'll inline as base64 (~vision-model limits + keeps requests sane).</summary>
    private const long MaxImageBytes = 10L * 1024 * 1024;

    /// <summary>True when <paramref name="s"/>, once normalised, has a known image extension.</summary>
    public static bool IsImagePath(string s)
    {
        var p = NormalizeDroppedPath(s);
        var ext = Path.GetExtension(p);
        return !string.IsNullOrEmpty(ext) && ImageMimeByExtension.ContainsKey(ext);
    }

    /// <summary>
    /// If <paramref name="s"/> is an existing image file within the size cap, read it and return a
    /// <c>data:</c> URI plus its file name. Best-effort — returns false (and the caller keeps the
    /// path as text) on anything unexpected: not an image, missing, too big, or unreadable.
    /// </summary>
    public static bool TryLoadImageDataUri(string s, out string dataUri, out string fileName)
    {
        dataUri = string.Empty;
        fileName = string.Empty;
        var path = NormalizeDroppedPath(s);
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || !ImageMimeByExtension.TryGetValue(ext, out var mime)) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxImageBytes) return false;
            var bytes = File.ReadAllBytes(path);
            dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            fileName = Path.GetFileName(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reconstruct the text of a drained key burst: printable chars as themselves, Enter as a
    /// newline, other control keys dropped. Used to turn a fast burst (a paste) back into text.
    /// </summary>
    public static string ReconstructBurst(IReadOnlyList<ConsoleKeyInfo> keys)
    {
        var sb = new StringBuilder(keys.Count);
        foreach (var k in keys)
        {
            if (k.Key == ConsoleKey.Enter) { sb.Append('\n'); continue; }
            if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
            else if (k.KeyChar == Esc) sb.Append(Esc); // keep so the marker stripper can see it
        }
        return sb.ToString();
    }
}
