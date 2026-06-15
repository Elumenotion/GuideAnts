using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Mcp;

public static class McpPublishedAssistantCatalog
{
    public const string ConversationGetToolName = "conversation_get";

    public static async Task<IReadOnlyList<McpAddressableAssistant>> LoadAsync(
        ApplicationDbContext db,
        Guid guideId,
        string? mcpDescription,
        CancellationToken cancellationToken = default)
    {
        var guide = await db.Assistants
            .AsNoTracking()
            .Include(a => a.CrewMembers)
                .ThenInclude(cm => cm.Assistant)
            .FirstOrDefaultAsync(a => a.Id == guideId, cancellationToken);

        if (guide == null)
            return [];

        var entries = new List<(Guid Id, string Name, string Description, bool IsGuide)>
        {
            (
                guide.Id,
                guide.Name,
                mcpDescription ?? guide.Description ?? string.Empty,
                true
            )
        };

        foreach (var member in guide.CrewMembers.OrderBy(cm => cm.DisplayOrder))
        {
            if (member.Assistant == null || string.IsNullOrWhiteSpace(member.Assistant.Name))
                continue;

            entries.Add((
                member.AssistantId,
                member.Assistant.Name,
                member.Assistant.Description ?? string.Empty,
                false));
        }

        return McpAssistantToolNaming.AssignToolNames(entries);
    }

    public static McpAddressableAssistant? FindByToolName(
        IReadOnlyList<McpAddressableAssistant> roster,
        string toolName)
    {
        return roster.FirstOrDefault(a =>
            string.Equals(a.ToolName, toolName, StringComparison.OrdinalIgnoreCase));
    }

    public static McpAddressableAssistant? FindByName(
        IReadOnlyList<McpAddressableAssistant> roster,
        string assistantName)
    {
        return roster.FirstOrDefault(a =>
            string.Equals(a.Name, assistantName, StringComparison.OrdinalIgnoreCase));
    }
}
