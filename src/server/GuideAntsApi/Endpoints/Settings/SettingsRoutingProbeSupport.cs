using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Endpoints.Settings;

internal static class SettingsRoutingProbeSupport
{
    /// <summary>R-9.2 <see cref="ChatTargetReadinessDto.ReferenceKind"/> for overview / batch preflight probes.</summary>
    public static string InferChatTargetReferenceKind(
        string modelId,
        IConfiguration configuration,
        bool anyAssistantsWithoutModel)
    {
        var defaultId = (configuration["ChatDefaults:DefaultModelId"] ?? string.Empty).Trim();
        var overrideAll = configuration.GetValue<bool>("ChatDefaults:OverrideAllChatModels");
        if (overrideAll
            && !string.IsNullOrEmpty(defaultId)
            && string.Equals(modelId, defaultId, StringComparison.Ordinal))
        {
            return "overriddenToDefault";
        }

        if (!overrideAll
            && anyAssistantsWithoutModel
            && !string.IsNullOrEmpty(defaultId)
            && string.Equals(modelId, defaultId, StringComparison.Ordinal))
        {
            return "defaultedTo";
        }

        return "direct";
    }

    /// <summary>
    /// Returns the distinct set of catalog model ids referenced by at least one
    /// active assistant (R-9.2 acceptance gate). Deduplicated and ordered for
    /// stable wire output.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetDistinctActiveAssistantModelIdsAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        return await db.Assistants
            .AsNoTracking()
            .Where(a => a.IsActive && a.ModelId != null)
            .Select(a => a.ModelId!)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Overview chat readiness should include:
    /// 1) any model referenced by an active assistant (even if stale/missing),
    /// 2) every active chat-capable catalog model (even if currently unused).
    /// This keeps the Overview list complete while preserving assistant counts.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetOverviewChatTargetModelIdsAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var assistantIds = await db.Assistants
            .AsNoTracking()
            .Where(a => a.IsActive && a.ModelId != null)
            .Select(a => a.ModelId!)
            .Distinct()
            .ToListAsync(cancellationToken);

        var activeCatalogChatIds = await db.Models
            .AsNoTracking()
            .Where(m => m.IsActive && (
                m.Provider == "openai-chat" ||
                m.Provider == "openai-responses" ||
                m.Provider == "azure-openai-chat" ||
                m.Provider == "azure-openai-responses" ||
                m.Provider == "anthropic" ||
                m.Provider == "llama-cpp"))
            .Select(m => m.ModelId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in assistantIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                merged.Add(id.Trim());
            }
        }

        foreach (var id in activeCatalogChatIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                merged.Add(id.Trim());
            }
        }

        return merged
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Parses <see cref="SettingsReadinessDto.GlobalBlockers"/> into per-section
    /// groupings for the Overview payload. Blockers are already of the form
    /// "Section:Field ...", so the section is the token before the first ':'.
    /// </summary>
    public static IReadOnlyList<ProviderConnectionIssueDto> BuildProviderIssues(SettingsReadinessDto readiness)
    {
        var all = readiness.Services.SelectMany(s => s.Blockers)
            .Concat(readiness.GlobalBlockers)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var bySection = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var blocker in all)
        {
            var colonIndex = blocker.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            var section = blocker[..colonIndex];
            if (!bySection.TryGetValue(section, out var list))
            {
                list = new List<string>();
                bySection[section] = list;
            }

            list.Add(blocker);
        }

        return bySection
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ProviderConnectionIssueDto(pair.Key, pair.Value))
            .ToList();
    }
}
