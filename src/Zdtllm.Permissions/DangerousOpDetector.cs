using System.Text.RegularExpressions;

namespace Zdtllm.Permissions;

/// <summary>
/// A hardcoded floor of shell operations that are catastrophic and effectively irreversible. These
/// require an interactive confirmation EVEN under a bypass / accept-edits / skip-permissions mode —
/// the one thing an over-eager model (GLM fires tool calls more freely than Claude) should never be
/// able to do silently. It does NOT override an explicit user allow rule; it only stops the
/// mode-driven auto-allow from covering these.
/// </summary>
public static class DangerousOpDetector
{
    // Per-sub-command patterns (checked against each decomposed segment).
    private static readonly Regex[] SegmentPatterns =
    [
        // rm -rf / , rm -rf ~ , rm -rf /* , rm -fr --no-preserve-root /
        new(@"\brm\s+(-[a-z]*r[a-z]*f|-[a-z]*f[a-z]*r|-r\s+-f|-f\s+-r)\b.*\s(/|~|/\*|\$HOME)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brm\s+.*--no-preserve-root", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // dd writing to a raw disk device
        new(@"\bdd\b.*\bof=/dev/(sd|nvme|hd|disk|mmcblk)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // mkfs / format a device
        new(@"\bmkfs(\.\w+)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // redirect into a raw disk device
        new(@">\s*/dev/(sd|nvme|hd|disk)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // chmod/chown -R on the filesystem root
        new(@"\bch(mod|own)\s+-[a-z]*R[a-z]*\s+\S+\s+/\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // git force-push (branch unknown → conservatively flag any force push)
        new(@"\bgit\s+push\b.*(--force(?!-with-lease)|(^|\s)-f(\s|$))", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // Whole-command patterns (span sub-commands / shell operators, so they must be checked before
    // decomposition — e.g. curl … | sh, or a fork bomb whose own text contains | ; &).
    private static readonly Regex[] WholePatterns =
    [
        new(@"\b(curl|wget|fetch)\b[^|]*\|\s*(sudo\s+)?(sh|bash|zsh|python|perl|ruby|node)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // fork bomb
        new(@":\(\)\s*\{\s*:\|:", RegexOptions.Compiled),
    ];

    public static bool IsDangerous(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        foreach (var w in WholePatterns)
            if (w.IsMatch(command)) return true;

        foreach (var seg in BashCommandDecomposer.Decompose(command))
            foreach (var p in SegmentPatterns)
                if (p.IsMatch(seg)) return true;

        return false;
    }
}
