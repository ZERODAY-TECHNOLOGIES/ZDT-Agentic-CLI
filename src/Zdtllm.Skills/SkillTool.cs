using System.Text;
using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Skills;

/// <summary>
/// The Skill tool. Takes a single argument <c>command</c> (the skill name) and
/// returns the skill's base path plus the markdown body of its SKILL.md. Auxiliary
/// files inside the skill directory (scripts/, references/, assets/) are NOT
/// loaded eagerly — the model can use Read on the returned base path to pull
/// them in only if needed.
/// </summary>
public sealed class SkillTool : ITool
{
    private readonly Dictionary<string, SkillDefinition> _skills;

    public SkillTool(IReadOnlyList<SkillDefinition> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        _skills = skills.ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    public ToolSchema Schema { get; } = new(
        Name: "Skill",
        Description: "Load a named skill's instructions. Returns the skill's base directory plus the body of its SKILL.md (the part after the YAML frontmatter). Use Read against the base directory afterwards if the skill body references auxiliary files.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "Exact skill name (case-sensitive, lowercase + digits + hyphens)." },
            },
            required = new[] { "command" },
        }));

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("Skill: missing 'command' parameter."));

        var name = c.GetString()!;
        if (!_skills.TryGetValue(name, out var skill))
        {
            var available = _skills.Count == 0
                ? "(none)"
                : string.Join(", ", _skills.Keys.OrderBy(k => k, StringComparer.Ordinal));
            return Task.FromResult(ToolResult.Error(
                $"Skill '{name}' not found. Available skills: {available}"));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"SKILL: {skill.Name}");
        sb.AppendLine($"BASE_DIR: {skill.BasePath}");
        sb.AppendLine();
        sb.Append(skill.Body);

        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}
