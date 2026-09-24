using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.EnvironmentVariables;

/// <summary>
/// Single source of truth for the ordered environment manifests handed to
/// <see cref="EnvironmentVariableConfigSerializer.DeserializeForExecution"/>.
///
/// Precedence (later manifest overrides earlier, per variable name):
///   1. Guide default environment  - Assistant.DefaultEnvironmentConfigJson
///      (applies to every project; seeded once, inherited by default)
///   2. Project environment for the guide  - ProjectAssistantEnvironment (guide, project)
///   3. Project environments for crew members, in GuideMember.DisplayOrder
///
/// Callers that previously built this order inline now use
/// <see cref="ResolveAsync"/> so the layering rule lives in exactly one place.
/// </summary>
public static class EnvironmentManifestResolver
{
    public static async Task<string?[]> ResolveAsync(
        ApplicationDbContext db,
        Guid projectId,
        Guid guideScopeId,
        CancellationToken cancellationToken)
    {
        var crewIds = await db.GuideMembers
            .AsNoTracking()
            .Where(member => member.GuideId == guideScopeId)
            .OrderBy(member => member.DisplayOrder ?? int.MaxValue)
            .ThenBy(member => member.Assistant.Name)
            .Select(member => member.AssistantId)
            .ToListAsync(cancellationToken);

        crewIds.Insert(0, guideScopeId);

        var projectManifests = await db.ProjectAssistantEnvironments
            .AsNoTracking()
            .Where(environment => environment.ProjectId == projectId
                && crewIds.Contains(environment.AssistantId))
            .Select(environment => new
            {
                environment.AssistantId,
                environment.EnvironmentConfigJson
            })
            .ToListAsync(cancellationToken);

        var manifestByAssistantId = projectManifests
            .ToDictionary(environment => environment.AssistantId, environment => environment.EnvironmentConfigJson);

        var ordered = crewIds
            .Select(assistantId => manifestByAssistantId.TryGetValue(assistantId, out var manifest) ? manifest : null)
            .Where(manifest => !string.IsNullOrWhiteSpace(manifest))
            .ToList();

        var defaultManifest = await db.Assistants
            .AsNoTracking()
            .Where(a => a.Id == guideScopeId)
            .Select(a => a.DefaultEnvironmentConfigJson)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(defaultManifest))
        {
            ordered.Insert(0, defaultManifest);
        }

        return ordered.ToArray();
    }
}
