using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Guides;

internal static class GuideExecutablePayload
{
    internal static readonly Guid RunPythonToolId = Guid.Parse("b0000000-0000-0000-0000-000000000009");

    internal static bool HasSkillScriptsPayload(IEnumerable<AssistantFile> files) =>
        files.Any(f =>
            string.Equals(f.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase)
            && SkillNotebookMaterializer.IsMaterializablePayloadPath(f.RelativePath));

    internal static void EnsureRunPythonToolForSkillPayload(Assistant assistant)
    {
        if (!HasSkillScriptsPayload(assistant.Files))
        {
            return;
        }

        if (assistant.Tools.Any(t => t.ToolId == RunPythonToolId))
        {
            return;
        }

        assistant.Tools.Add(new AssistantTool
        {
            AssistantId = assistant.Id,
            ToolId = RunPythonToolId,
        });
    }

    internal static async Task EnsureRunPythonToolForSkillPayloadAsync(
        ApplicationDbContext context,
        Guid assistantId,
        CancellationToken cancellationToken = default)
    {
        var files = await context.AssistantFiles
            .AsNoTracking()
            .Where(f => f.AssistantId == assistantId)
            .ToListAsync(cancellationToken);

        if (!HasSkillScriptsPayload(files))
        {
            return;
        }

        var hasRunPython = await context.AssistantTools
            .AnyAsync(
                t => t.AssistantId == assistantId && t.ToolId == RunPythonToolId,
                cancellationToken);

        if (hasRunPython)
        {
            return;
        }

        await context.AssistantTools.AddAsync(
            new AssistantTool
            {
                AssistantId = assistantId,
                ToolId = RunPythonToolId,
            },
            cancellationToken);
    }
}
