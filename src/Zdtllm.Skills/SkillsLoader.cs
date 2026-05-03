using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Zdtllm.Skills;

/// <summary>
/// Discovers SKILL.md files under the user-level (<c>~/.zdtllm/skills/</c>) and
/// project-level (<c>&lt;cwd&gt;/.zdtllm/skills/</c>) skill directories. Each skill lives
/// in <c>&lt;skills-root&gt;/&lt;name&gt;/SKILL.md</c>; the directory name and the YAML
/// frontmatter <c>name</c> field must match. Project skills override user skills with
/// the same name.
/// </summary>
public sealed partial class SkillsLoader
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    private static readonly IDeserializer YamlDeserializer =
        new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    [GeneratedRegex(@"\A---\s*\r?\n(.*?)\r?\n---\s*\r?\n(.*)\z", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^[a-z0-9-]+$")]
    private static partial Regex ValidNameRegex();

    /// <summary>
    /// Returns all skills discovered for the given working directory, ordered by name.
    /// Skips skills that fail validation (invalid name, missing description, mismatched
    /// directory name, malformed SKILL.md) silently — wiring the agent must not fail
    /// because of one broken skill. Project-level skills override user-level skills
    /// with the same name.
    /// </summary>
    /// <param name="cwd">Project working directory (project skills live under cwd/.zdtllm/skills/).</param>
    /// <param name="options">Override the user-level skills root (used by tests; defaults to ~/.zdtllm/skills).</param>
    public IReadOnlyList<SkillDefinition> Discover(string cwd, SkillsLoaderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cwd);

        var byName = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);

        var userRoot = options?.UserSkillsRoot ?? DefaultUserSkillsRoot();
        var projectRoot = Path.Combine(cwd, ".zdtllm", "skills");

        DiscoverFrom(userRoot, byName);
        DiscoverFrom(projectRoot, byName);

        return byName.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    public static string DefaultUserSkillsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zdtllm",
        "skills");

    private static void DiscoverFrom(string skillsRoot, Dictionary<string, SkillDefinition> sink)
    {
        if (!Directory.Exists(skillsRoot)) return;

        foreach (var skillDir in Directory.EnumerateDirectories(skillsRoot))
        {
            var skillMdPath = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillMdPath)) continue;

            try
            {
                var skill = LoadOne(skillMdPath);
                if (skill is not null) sink[skill.Name] = skill;
            }
            catch
            {
                // Malformed skill — drop it rather than fail the whole agent.
            }
        }
    }

    private static SkillDefinition? LoadOne(string skillMdPath)
    {
        var text = File.ReadAllText(skillMdPath);
        var match = FrontmatterRegex().Match(text);
        if (!match.Success) return null;

        var frontmatterYaml = match.Groups[1].Value;
        var body = match.Groups[2].Value.Trim();

        Dictionary<string, string>? frontmatter;
        try
        {
            frontmatter = YamlDeserializer.Deserialize<Dictionary<string, string>>(frontmatterYaml);
        }
        catch
        {
            return null;
        }

        if (frontmatter is null) return null;
        if (!frontmatter.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            return null;
        if (!frontmatter.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
            return null;

        if (!IsValidName(name)) return null;
        if (description.Length > MaxDescriptionLength) return null;

        var basePath = Path.GetDirectoryName(skillMdPath)!;
        var dirName = Path.GetFileName(basePath);
        if (!string.Equals(name, dirName, StringComparison.Ordinal)) return null;

        return new SkillDefinition(name, description, basePath, body);
    }

    internal static bool IsValidName(string name) =>
        name.Length is > 0 and <= MaxNameLength && ValidNameRegex().IsMatch(name);
}

public sealed record SkillsLoaderOptions
{
    /// <summary>
    /// Override for the user-level skills root. Defaults to <c>~/.zdtllm/skills/</c>.
    /// Set in tests to an isolated temp directory so production state isn't read.
    /// </summary>
    public string? UserSkillsRoot { get; init; }
}
