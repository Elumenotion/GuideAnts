using System.Text;

namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// Builds the tier-1 skills discovery block for prompt injection (S3).
/// </summary>
public static class SkillDiscoveryBlockBuilder
{
    public static string? BuildDiscoveryBlock(IReadOnlyList<SkillDescriptor> visibleSkills)
    {
        if (visibleSkills.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Skills");
        sb.AppendLine(
            "A skill is a set of instructions provided through a SKILL.md source. If the task " +
            "matches a skill's description, call skills.read on its locator and follow it " +
            "before acting.");
        sb.AppendLine();
        sb.AppendLine("### Available skills");

        foreach (var skill in visibleSkills)
        {
            sb.Append("- ");
            sb.Append(skill.Name);
            sb.Append(": ");
            sb.Append(skill.Description);
            sb.Append(" (orchestrator resource: ");
            sb.Append(skill.Locator);
            sb.AppendLine(")");
        }

        sb.AppendLine();
        sb.AppendLine("### How to use skills");
        sb.AppendLine("- Call skills.list to see available skills for this assistant.");
        sb.AppendLine(
            "- Call skills.read with a locator (and optional file_path for references/foo.md) " +
            "to load the full instructions.");
        sb.AppendLine("- Only proceed without loading a skill if none are relevant.");
        sb.AppendLine(
            "- Skill scripts and assets are copied into the notebook at creation under " +
            "Output/Skills/<name>/ (from sandbox CWD). Run them with sandbox/terminal tools; " +
            "load SKILL.md and references on demand via skills.read.");

        return sb.ToString().TrimEnd();
    }
}
