using System.Text.Json;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.AssistantDefinitions;
using AntRunner.ToolCalling.Attributes;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Skills;

namespace GuideAntsApi.Services;

/// <summary>
/// Server-handled skill discovery and on-demand body loading tools (S4).
/// </summary>
public static class SkillTools
{
    private static IServiceProvider? _provider;

    public static void InitializeServiceProvider(IServiceProvider provider)
    {
        _provider = provider;
    }

    [Tool(
        OperationId = "skills.list",
        Summary = "List the skills available to this assistant (name, description, locator, files)."
    )]
    [RequiresNotebookContext]
    public static Task<string> ListSkills(
        [Parameter(Description = "Assistant definition", Hidden = true)] AssistantDefinition? assistantDefinition = null,
        [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
    {
        if (assistantDefinition == null)
        {
            return Task.FromResult(JsonError("Assistant definition is required."));
        }

        if (assistantDefinition.Skills == null || assistantDefinition.Skills.Count == 0)
        {
            return Task.FromResult(JsonSerializer.Serialize(Array.Empty<SkillDescriptor>()));
        }

        var visible = SkillVisibilityFilter.FilterVisibleSkills(assistantDefinition);
        return Task.FromResult(JsonSerializer.Serialize(visible));
    }

    [Tool(
        OperationId = "skills.read",
        Summary = "Read a skill's SKILL.md body, or a specific reference/script/asset file."
    )]
    [RequiresNotebookContext]
    public static async Task<string> ReadSkill(
        [Parameter(Description = "skill://<assistantId>/<name> or skill://<guide>/<name>")] string locator,
        [Parameter(Description = "Optional path within the skill, e.g. references/foo.md")] string? file_path = null,
        [Parameter(Description = "Assistant definition", Hidden = true)] AssistantDefinition? assistantDefinition = null,
        [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return JsonError("locator is required.");
        }

        if (assistantDefinition == null)
        {
            return JsonError("Assistant definition is required.");
        }

        if (!SkillLocator.TryParse(locator, out var parts))
        {
            return JsonError($"Invalid skill locator '{locator}'. Expected skill://<assistantId>/<name> or skill://<guide>/<name>.");
        }

        Guid assistantId;
        string skillName;
        string? effectiveFilePath = file_path;

        if (parts.IsPublished)
        {
            if (assistantDefinition.Name == null
                || !string.Equals(parts.GuideName, assistantDefinition.Name, StringComparison.OrdinalIgnoreCase))
            {
                return JsonError($"Skill locator guide '{parts.GuideName}' does not match the current assistant.");
            }

            assistantId = assistantDefinition.Id
                ?? throw new InvalidOperationException("Assistant definition id is required for published skill reads.");
            skillName = parts.SkillName;

            if (!string.IsNullOrWhiteSpace(parts.ReferenceRelativePath))
            {
                if (!string.IsNullOrWhiteSpace(file_path))
                {
                    return JsonError("Use either a reference locator or file_path, not both.");
                }

                effectiveFilePath = parts.ReferenceRelativePath.StartsWith("references/", StringComparison.OrdinalIgnoreCase)
                    ? parts.ReferenceRelativePath
                    : $"references/{parts.ReferenceRelativePath}";
            }
        }
        else
        {
            assistantId = parts.AssistantId!.Value;
            skillName = parts.SkillName;

            if (assistantDefinition.Id.HasValue && assistantDefinition.Id.Value != assistantId)
            {
                return JsonError("Skill locator assistant id does not match the current assistant.");
            }
        }

        var descriptor = assistantDefinition.Skills?
            .FirstOrDefault(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(s.Locator, locator, StringComparison.OrdinalIgnoreCase));
        if (descriptor == null)
        {
            return JsonError($"Unknown skill '{skillName}' for this assistant.");
        }

        string relativePath;
        try
        {
            relativePath = SkillPathSafety.ResolveUnderSkillFolder(descriptor.FolderPath, effectiveFilePath);
        }
        catch (InvalidOperationException ex)
        {
            return JsonError(ex.Message);
        }

        if (!descriptor.Files.Any(f => string.Equals(f, relativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return JsonError($"Skill file '{relativePath}' was not found for skill '{skillName}'.");
        }

        if (_provider == null)
        {
            throw new InvalidOperationException("SkillTools service provider is not initialized.");
        }

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var read = await SkillContentReader.TryReadAsync(
            db,
            assistantId,
            descriptor,
            effectiveFilePath,
            cancellationToken);

        if (read == null)
        {
            return JsonError($"Skill file '{relativePath}' has no content.");
        }

        return JsonSerializer.Serialize(new { path = read.RelativePath, content = read.Content });
    }

    private static string JsonError(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
