namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// Offer-time visibility filter for skill discovery and <c>skills_list</c> (S6).
/// </summary>
public static class SkillVisibilityFilter
{
    public static IReadOnlySet<string> GetAvailableToolNames(AssistantDefinition definition)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (definition.Tools != null)
        {
            foreach (var tool in definition.Tools)
            {
                if (!string.IsNullOrWhiteSpace(tool.Type))
                {
                    names.Add(tool.Type);
                }

                var functionName = tool.Function?.AsObject?.Name;
                if (!string.IsNullOrWhiteSpace(functionName))
                {
                    names.Add(functionName);
                }
            }
        }

        return names;
    }

    public static List<SkillDescriptor> FilterVisibleSkills(
        AssistantDefinition definition,
        string? currentPlatform = null)
    {
        if (definition.Skills == null || definition.Skills.Count == 0)
        {
            return [];
        }

        var platform = currentPlatform ?? GetCurrentPlatform();
        var availableTools = GetAvailableToolNames(definition);

        return definition.Skills
            .Where(skill => IsVisible(skill, availableTools, platform))
            .ToList();
    }

    public static bool IsVisible(
        SkillDescriptor skill,
        IReadOnlySet<string> availableTools,
        string currentPlatform)
    {
        if (skill.Platforms is { Count: > 0 }
            && !skill.Platforms.Any(p => string.Equals(p, currentPlatform, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (skill.RequiresToolsets is { Count: > 0 }
            && skill.RequiresToolsets.Any(toolset => !SkillToolsetMapping.IsToolsetAvailable(toolset, availableTools)))
        {
            return false;
        }

        if (skill.RequiresTools is { Count: > 0 }
            && skill.RequiresTools.Any(tool => !availableTools.Contains(tool)))
        {
            return false;
        }

        if (skill.FallbackForToolsets is { Count: > 0 }
            && skill.FallbackForToolsets.Any(toolset => SkillToolsetMapping.IsToolsetAvailable(toolset, availableTools)))
        {
            return false;
        }

        if (skill.FallbackForTools is { Count: > 0 }
            && skill.FallbackForTools.Any(availableTools.Contains))
        {
            return false;
        }

        return true;
    }

    private static string GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        return "linux";
    }
}
