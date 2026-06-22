using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsOverviewEndpoints
{
    public static void MapSettingsOverviewEndpoints(this WebApplication app)
    {
        var group = SettingsGroupFactory.MapCoreGroup(app);

        group.MapGet("/overview", async (
            IApplicationSettingsService settingsService,
            IRoutingReadinessService readiness,
            ILlamaRuntimeInventoryService inventoryService,
            ApplicationDbContext db,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var serviceRollups = new List<ServiceModeRollupDto>(RoutedServiceNames.All.Count);
            foreach (var serviceName in RoutedServiceNames.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modes = await settingsService.GetServiceModesAsync(serviceName, cancellationToken);
                var probed = new List<ModeReadinessDto>(modes.Count);
                foreach (var mode in modes)
                {
                    probed.Add(await readiness.ProbeModeAsync(serviceName, mode.ModeId, cancellationToken));
                }

                var ready = probed.Count(m => string.Equals(m.Status, "ready", StringComparison.Ordinal));
                serviceRollups.Add(new ServiceModeRollupDto(
                    Service: serviceName,
                    Ready: ready,
                    Total: probed.Count,
                    Modes: probed));
            }

            var overviewChatTargets = await SettingsRoutingProbeSupport.GetOverviewChatTargetModelIdsAsync(db, cancellationToken);
            var overviewList = overviewChatTargets.ToList();
            var defaultModelId = (configuration["ChatDefaults:DefaultModelId"] ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(defaultModelId)
                && !overviewList.Any(id => string.Equals(id, defaultModelId, StringComparison.Ordinal)))
            {
                overviewList.Add(defaultModelId);
                overviewList.Sort(StringComparer.Ordinal);
            }

            var anyAssistantsWithoutModel = await db.Assistants
                .AsNoTracking()
                .AnyAsync(a => a.IsActive && a.ModelId == null, cancellationToken);
            var chatTargetResults = new List<ChatTargetReadinessDto>(overviewList.Count);
            foreach (var modelId in overviewList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kind = SettingsRoutingProbeSupport.InferChatTargetReferenceKind(modelId, configuration, anyAssistantsWithoutModel);
                chatTargetResults.Add(await readiness.ProbeChatTargetAsync(modelId, cancellationToken, kind));
            }

            var chatRollup = new ChatTargetsRollupDto(
                Ready: chatTargetResults.Count(t => string.Equals(t.Status, "ready", StringComparison.Ordinal)),
                Total: chatTargetResults.Count,
                Targets: chatTargetResults);

            var readinessDto = await settingsService.GetReadinessAsync(cancellationToken);
            var providerIssues = SettingsRoutingProbeSupport.BuildProviderIssues(readinessDto);

            IReadOnlyList<LlamaRuntimeInventoryItemDto> inventoryItems = [];
            if (RuntimeConfigurationPlaceholders.HasUsableUrl(configuration["LlamaCpp:BaseUrl"]))
            {
                try
                {
                    inventoryItems = await inventoryService.GetInventoryAsync(cancellationToken);
                }
                catch
                {
                    inventoryItems = [];
                }
            }
            var loadedAliases = inventoryItems.Count(i => string.Equals(i.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase));
            var missingArtifacts = inventoryItems
                .Where(i => !i.HasModelFile)
                .Select(i => i.RouterModelId)
                .ToList();

            var runtimeSnapshot = new LlamaRuntimeSnapshotDto(
                LoadedAliases: loadedAliases,
                TotalAliases: inventoryItems.Count,
                MissingArtifactAliases: missingArtifacts);

            return Results.Ok(new SettingsOverviewDto(
                GeneratedUtc: DateTime.UtcNow,
                ServiceModeReadiness: serviceRollups,
                ChatTargets: chatRollup,
                ProviderIssues: providerIssues,
                LlamaRuntime: runtimeSnapshot));
        })
        .WithName("GetSettingsOverview")
        .Produces<SettingsOverviewDto>(StatusCodes.Status200OK);
    }
}
