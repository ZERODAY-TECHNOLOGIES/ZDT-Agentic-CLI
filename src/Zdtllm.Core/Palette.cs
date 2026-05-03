namespace Zdtllm.Core;

/// <summary>
/// Brand palette mirroring zer0day.ro: cyan (#1BEACD) and gold (#E5D936) on dark
/// navy (#132431), with muted blue-grays for secondary text and red (#ef4444) for
/// errors. Codes are 24-bit truecolor — any modern terminal renders them, and
/// piped output simply keeps them as raw bytes (substring assertions still match
/// the textual content between the codes, so test stays plain-string-friendly).
/// </summary>
internal static class Palette
{
    public const string Reset = "[0m";
    public const string Bold = "[1m";

    private const string CyanFg = "[38;2;27;234;205m";
    private const string GoldFg = "[38;2;229;217;54m";
    private const string RedFg = "[38;2;239;68;68m";
    private const string BodyFg = "[38;2;232;237;242m";
    private const string DimFg = "[38;2;170;185;200m";
    private const string MuteFg = "[38;2;104;123;137m";

    public static string Cyan(string s) => $"{CyanFg}{s}{Reset}";
    public static string Gold(string s) => $"{GoldFg}{s}{Reset}";
    public static string Red(string s) => $"{RedFg}{s}{Reset}";
    public static string Body(string s) => $"{BodyFg}{s}{Reset}";
    public static string Dim(string s) => $"{DimFg}{s}{Reset}";
    public static string Mute(string s) => $"{MuteFg}{s}{Reset}";

    public static string CyanBold(string s) => $"{Bold}{CyanFg}{s}{Reset}";
    public static string GoldBold(string s) => $"{Bold}{GoldFg}{s}{Reset}";
    public static string BodyBold(string s) => $"{Bold}{BodyFg}{s}{Reset}";

    /// <summary>Two-tone bar: filled cells in cyan, empty cells in muted.</summary>
    public static string Bar(int filled, int total, int width)
    {
        if (total <= 0 || width <= 0) return string.Empty;
        var ratio = Math.Clamp((double)filled / total, 0, 1);
        var on = (int)Math.Round(ratio * width);
        var off = width - on;
        return Cyan(new string('▰', on)) + Mute(new string('▱', off));
    }
}
