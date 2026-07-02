using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides.Skills;

/// <summary>
/// Explicit S9 mapping from skill prerequisites to concrete GuideAnts capabilities.
/// </summary>
public static class SkillPrerequisiteMapper
{
    private static readonly Dictionary<string, Guid> CatalogToolIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["WebSearch"] = Guid.Parse("b0000000-0000-0000-0000-00000000000d"),
            ["ReadWeb"] = Guid.Parse("b0000000-0000-0000-0000-00000000000e"),
            ["run_bash"] = Guid.Parse("b0000000-0000-0000-0000-000000000008"),
            ["run_python"] = Guid.Parse("b0000000-0000-0000-0000-000000000009"),
        };

    public static SkillPrerequisiteMappingResult Map(SkillFrontmatter frontmatter)
    {
        var toolIds = new HashSet<Guid>();
        var needsCodeInterpreter = false;
        var summary = new List<SkillPrerequisiteMappingDto>();

        foreach (var toolset in frontmatter.RequiresToolsets)
        {
            if (!SkillToolsetMapping.All.TryGetValue(toolset, out var mappedTools))
            {
                summary.Add(new SkillPrerequisiteMappingDto(
                    $"requires_toolsets: {toolset}",
                    "(unmapped)",
                    $"Unknown toolset '{toolset}' has no explicit mapping."));
                continue;
            }

            foreach (var mapped in mappedTools)
            {
                if (string.Equals(mapped, "code_interpreter", StringComparison.OrdinalIgnoreCase))
                {
                    needsCodeInterpreter = true;
                    summary.Add(new SkillPrerequisiteMappingDto(
                        $"requires_toolsets: {toolset}",
                        "code_interpreter",
                        $"Toolset '{toolset}' requires code interpreter capability."));
                    continue;
                }

                if (CatalogToolIds.TryGetValue(mapped, out var toolId))
                {
                    toolIds.Add(toolId);
                    summary.Add(new SkillPrerequisiteMappingDto(
                        $"requires_toolsets: {toolset}",
                        mapped,
                        $"Toolset '{toolset}' maps to catalog tool '{mapped}'."));
                }
            }
        }

        foreach (var tool in frontmatter.RequiresTools)
        {
            if (CatalogToolIds.TryGetValue(tool, out var toolId))
            {
                toolIds.Add(toolId);
                summary.Add(new SkillPrerequisiteMappingDto(
                    $"requires_tools: {tool}",
                    tool,
                    $"Declared tool '{tool}' maps to catalog tool '{tool}'."));
                continue;
            }

            summary.Add(new SkillPrerequisiteMappingDto(
                $"requires_tools: {tool}",
                "(unmapped)",
                $"Declared tool '{tool}' has no explicit catalog mapping."));
        }

        return new SkillPrerequisiteMappingResult(toolIds.ToList(), needsCodeInterpreter, summary);
    }
}

public sealed record SkillPrerequisiteMappingResult(
    List<Guid> ToolIds,
    bool NeedsCodeInterpreter,
    List<SkillPrerequisiteMappingDto> Summary);
