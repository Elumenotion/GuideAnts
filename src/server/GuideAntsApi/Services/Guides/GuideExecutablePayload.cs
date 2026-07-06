using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Guides;

internal static class GuideExecutablePayload
{
    internal static readonly Guid RunPythonToolId = Guid.Parse("b0000000-0000-0000-0000-000000000009");

    internal const string FilesContextOptionKey = "files";
    internal const string FilesContextOptionValue = "[@files]";

    internal static bool HasSkillScriptsPayload(IEnumerable<AssistantFile> files) =>
        files.Any(f =>
            string.Equals(f.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase)
            && SkillNotebookMaterializer.IsMaterializablePayloadPath(f.RelativePath));

    internal static bool HasNotebookPayloadFiles(IEnumerable<AssistantFile> files) =>
        files.Any(IsNotebookPayloadFile);

    internal static bool NewUploadsHaveNotebookPayload(IEnumerable<FileUploadDto> uploads) =>
        uploads.Any(IsNotebookPayloadUpload);

    internal static bool HasFilesContextOption(IEnumerable<AssistantContextOption> options) =>
        options.Any(o =>
            o.Value != null
            && o.Value.Contains(FilesContextOptionValue, StringComparison.OrdinalIgnoreCase));

    private static bool IsNotebookPayloadFile(AssistantFile file) =>
        string.Equals(file.FolderKind, "CodeInterpreter", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(file.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase)
            && SkillNotebookMaterializer.IsMaterializablePayloadPath(file.RelativePath));

    private static bool IsNotebookPayloadUpload(FileUploadDto file) =>
        string.Equals(file.FolderKind, "CodeInterpreter", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(file.FolderKind, "Skill", StringComparison.OrdinalIgnoreCase)
            && SkillNotebookMaterializer.IsMaterializablePayloadPath(file.RelativePath));

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

    internal static void EnsureFilesContextOption(Assistant assistant)
    {
        if (!HasNotebookPayloadFiles(assistant.Files))
        {
            return;
        }

        if (HasFilesContextOption(assistant.ContextOptions))
        {
            return;
        }

        assistant.ContextOptions.Add(new AssistantContextOption
        {
            AssistantId = assistant.Id,
            Key = FilesContextOptionKey,
            Value = FilesContextOptionValue,
            Created = DateTime.UtcNow,
        });
    }

    internal static async Task EnsureFilesContextOptionAsync(
        ApplicationDbContext context,
        Guid assistantId,
        CancellationToken cancellationToken = default)
    {
        var optionValues = await context.AssistantContextOptions
            .Where(o => o.AssistantId == assistantId)
            .Select(o => o.Value)
            .ToListAsync(cancellationToken);

        if (optionValues.Any(v =>
                v != null
                && v.Contains(FilesContextOptionValue, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var files = await context.AssistantFiles
            .AsNoTracking()
            .Where(f => f.AssistantId == assistantId)
            .ToListAsync(cancellationToken);

        if (!HasNotebookPayloadFiles(files))
        {
            return;
        }

        await context.AssistantContextOptions.AddAsync(
            new AssistantContextOption
            {
                AssistantId = assistantId,
                Key = FilesContextOptionKey,
                Value = FilesContextOptionValue,
                Created = DateTime.UtcNow,
            },
            cancellationToken);
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
