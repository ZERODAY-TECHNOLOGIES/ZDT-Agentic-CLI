using System.Text;
using Zdtllm.Skills;

namespace Zdtllm.Core;

/// <summary>
/// Pure composer for the agent's bootstrap system prompt. No filesystem or env access —
/// every input is supplied by the caller. The output stitches together (in this order):
///
///   1. Base text (the default zdtllmcli prompt unless --system-prompt[-file] replaced it).
///   2. Optional appended text (from --append-system-prompt[-file]).
///   3. ZDTLLM.md project memory block, if non-null.
///   4. Additional accessible directories block, if any are listed.
///   5. <available_skills> block, if any skills are loaded.
/// </summary>
public static class SystemPromptComposer
{
    public static string Compose(
        string baseText,
        string? appendText = null,
        string? memoryFile = null,
        IReadOnlyList<string>? additionalDirectories = null,
        IReadOnlyList<SkillDefinition>? skills = null)
    {
        ArgumentNullException.ThrowIfNull(baseText);

        var sb = new StringBuilder(baseText.TrimEnd());

        if (!string.IsNullOrWhiteSpace(appendText))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(appendText.Trim());
        }

        if (!string.IsNullOrWhiteSpace(memoryFile))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("# Project memory (ZDTLLM.md)");
            sb.AppendLine();
            sb.Append(memoryFile.Trim());
        }

        if (additionalDirectories is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("# Additional accessible directories");
            sb.AppendLine();
            sb.AppendLine("Beyond the current working directory, you may also operate within:");
            foreach (var dir in additionalDirectories)
                sb.AppendLine($"- {dir}");
        }

        if (skills is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("<available_skills>");
            foreach (var skill in skills)
                sb.AppendLine($"- {skill.Name}: {skill.Description}");
            sb.AppendLine("To load a skill's instructions, call the Skill tool with command=<skill-name>.");
            sb.Append("</available_skills>");
        }

        return sb.ToString();
    }
}
