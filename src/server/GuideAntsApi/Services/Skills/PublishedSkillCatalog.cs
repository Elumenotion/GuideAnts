using AntRunner.Chat;
using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Services.Skills;

public sealed record PublishedSkillEntry(
    SkillDescriptor Descriptor,
    Guid AssistantId,
    string PublishedLocator);

/// <summary>
/// Lists skill resources for a published guide (guide-scoped, gating-consistent with discovery).
/// </summary>
public static class PublishedSkillCatalog
{
    public static async Task<IReadOnlyList<PublishedSkillEntry>> ListVisibleSkillsAsync(
        string guideName,
        IReadOnlyList<McpAddressableAssistant> assistants,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(guideName))
        {
            throw new InvalidOperationException("Guide name is required to list published skills.");
        }

        var byName = new Dictionary<string, PublishedSkillEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var assistant in assistants)
        {
            var definition = await AssistantUtility.GetAssistantCreateRequest(assistant.Name);
            if (definition?.Skills is not { Count: > 0 })
            {
                continue;
            }

            foreach (var skill in SkillVisibilityFilter.FilterVisibleSkills(definition))
            {
                AddOrPreferGuideAssistant(byName, assistants, assistant, skill, guideName);
            }
        }

        return byName.Values
            .OrderBy(e => e.Descriptor.DisplayOrder)
            .ThenBy(e => e.Descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<PublishedSkillEntry?> FindSkillForReadAsync(
        string guideName,
        IReadOnlyList<McpAddressableAssistant> assistants,
        string skillName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(guideName))
        {
            throw new InvalidOperationException("Guide name is required to resolve published skills.");
        }

        var byName = new Dictionary<string, PublishedSkillEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var assistant in assistants)
        {
            var definition = await AssistantUtility.GetAssistantCreateRequest(assistant.Name);
            if (definition?.Skills is not { Count: > 0 })
            {
                continue;
            }

            foreach (var skill in definition.Skills)
            {
                AddOrPreferGuideAssistant(byName, assistants, assistant, skill, guideName);
            }
        }

        return byName.TryGetValue(skillName, out var entry) ? entry : null;
    }

    private static void AddOrPreferGuideAssistant(
        Dictionary<string, PublishedSkillEntry> byName,
        IReadOnlyList<McpAddressableAssistant> assistants,
        McpAddressableAssistant assistant,
        SkillDescriptor skill,
        string guideName)
    {
        var locator = SkillLocator.FormatPublished(guideName, skill.Name);
        var entry = new PublishedSkillEntry(skill, assistant.AssistantId, locator);

        if (!byName.TryGetValue(skill.Name, out var existing))
        {
            byName[skill.Name] = entry;
            return;
        }

        if (assistant.IsGuide)
        {
            byName[skill.Name] = entry;
            return;
        }

        var existingAssistant = assistants.First(a => a.AssistantId == existing.AssistantId);
        if (!existingAssistant.IsGuide)
        {
            byName[skill.Name] = entry;
        }
    }
}
