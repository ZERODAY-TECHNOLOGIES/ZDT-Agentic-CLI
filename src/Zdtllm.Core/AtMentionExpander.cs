using System.Text;
using System.Text.RegularExpressions;

namespace Zdtllm.Core;

/// <summary>
/// Expands <c>@path</c> mentions in a user prompt by inlining the referenced file's contents (or a
/// directory listing) so the model gets the file without a Read round-trip — claude-cli's @-mention.
/// Only mentions that resolve to a real file/dir on disk are expanded; anything else (an email
/// address, an @handle, a typo) is left untouched, so false positives are impossible.
/// </summary>
public static class AtMentionExpander
{
    // @ must start a token (preceded by start-of-string, whitespace, or an opening bracket/quote) so
    // "office@zer0day.ro" doesn't match. The path token runs to the next whitespace or @.
    private static readonly Regex Mention = new(@"(?<![^\s(<""'])@([^\s@]+)", RegexOptions.Compiled);

    public static string Expand(string prompt, string cwd, int maxFileBytes = 65536)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.IndexOf('@') < 0) return prompt;

        var appended = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in Mention.Matches(prompt))
        {
            // Trim trailing punctuation that's likely sentence syntax, not part of the path.
            var token = m.Groups[1].Value.TrimEnd('.', ',', ')', ':', ';', '!', '?', '"', '\'', '>');
            if (token.Length == 0 || !seen.Add(token)) continue;

            string full;
            try { full = Path.IsPathRooted(token) ? token : Path.GetFullPath(Path.Combine(cwd, token)); }
            catch { continue; }

            try
            {
                if (File.Exists(full)) AppendFile(appended, token, full, cwd, maxFileBytes);
                else if (Directory.Exists(full)) AppendDir(appended, token, full);
                // else: not a real path — leave the @token in the prompt as literal text.
            }
            catch { /* unreadable — skip, never fail the turn over a mention */ }
        }

        return appended.Length == 0 ? prompt : prompt + "\n\n" + appended.ToString().TrimEnd();
    }

    private static void AppendFile(StringBuilder sb, string token, string full, string cwd, int maxFileBytes)
    {
        // Binary sniff: don't inline a compiled artefact as mojibake.
        using (var fs = File.OpenRead(full))
        {
            Span<byte> probe = stackalloc byte[Math.Min(4096, (int)Math.Min(fs.Length, 4096))];
            var read = fs.Read(probe);
            for (var i = 0; i < read; i++)
                if (probe[i] == 0)
                {
                    sb.Append("--- @").Append(token).AppendLine(" (binary file — not inlined) ---").AppendLine();
                    return;
                }
        }

        var content = File.ReadAllText(full);
        var truncated = content.Length > maxFileBytes;
        if (truncated) content = content[..maxFileBytes];

        string rel;
        try { rel = Path.GetRelativePath(cwd, full); } catch { rel = full; }

        sb.Append("--- @").Append(token).Append(" (").Append(rel).AppendLine(") ---");
        sb.AppendLine("```");
        sb.AppendLine(content.TrimEnd());
        sb.AppendLine("```");
        if (truncated) sb.Append("[truncated at ").Append(maxFileBytes).AppendLine(" bytes]");
        sb.AppendLine();
    }

    private static void AppendDir(StringBuilder sb, string token, string full)
    {
        sb.Append("--- @").Append(token).AppendLine(" (directory listing) ---");
        var count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(full).OrderBy(e => e, StringComparer.Ordinal))
        {
            if (count++ >= 200) { sb.AppendLine("… (listing truncated at 200 entries)"); break; }
            var name = Path.GetFileName(entry);
            sb.Append("- ").Append(name).AppendLine(Directory.Exists(entry) ? "/" : "");
        }
        sb.AppendLine();
    }
}
