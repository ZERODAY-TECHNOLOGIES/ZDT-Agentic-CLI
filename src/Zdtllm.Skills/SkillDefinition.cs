namespace Zdtllm.Skills;

/// <summary>
/// One discovered skill. The Body is the markdown that follows the YAML
/// frontmatter in SKILL.md (no trailing or leading blank lines).
/// </summary>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string BasePath,
    string Body);
