using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Reachability;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsOverviewEndpoints
{
    public static void MapSettingsOverviewEndpoints(this WebApplication app)
    {
        var group = SettingsGroupFactory.MapCoreGroup(app);

        // Configuration surface (R-A): renders completely and instantaneously whether
        // max:8112 is up, down, blackholed, or half-dead (R1). Every readiness call names
        // NonBlocking explicitly (T-A1); runtime/presence fields come from synchronous
        // Plane C reads. No code path in this handler can wait on a socket.
        group.MapGet("/overview", async (
            IApplicationSettingsService settingsService,
            IRoutingReadinessService readiness,
            ILocalEndpointCache endpointCache,
            ILocalInventoryMirror inventoryMirror,
            ApplicationDbContext db,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            // Service rollups: concurrent across services AND modes, all NonBlocking.
            var serviceRollupTasks = RoutedServiceNames.All.Select(async serviceName =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modes = await settingsService.GetServiceModesAsync(serviceName, cancellationToken);
                var probed = await Task.WhenAll(modes.Select(mode =>
                    readiness.ProbeModeAsync(serviceName, mode.ModeId, cancellationToken, RoutingProbeMode.NonBlocking)));
                var ready = probed.Count(m => string.Equals(m.Status, "ready", StringComparison.Ordinal));
                return new ServiceModeRollupDto(
                    Service: serviceName,
                    Ready: ready,
                    Total: probed.Length,
                    Modes: probed);
            });
            var serviceRollups = await Task.WhenAll(serviceRollupTasks);

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

            // Chat targets: concurrent, NonBlocking. The readiness column shows known state
            // with an observed timestamp instead of going blank (F-2).
            var chatTargetResults = await Task.WhenAll(overviewList.Select(modelId =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kind = SettingsRoutingProbeSupport.InferChatTargetReferenceKind(modelId, configuration, anyAssistantsWithoutModel);
                return readiness.ProbeChatTargetAsync(modelId, cancellationToken, kind, RoutingProbeMode.NonBlocking);
            }));

            var chatRollup = new ChatTargetsRollupDto(
                Ready: chatTargetResults.Count(t => string.Equals(t.Status, "ready", StringComparison.Ordinal)),
                Total: chatTargetResults.Length,
                Targets: chatTargetResults);

            var readinessDto = await settingsService.GetReadinessAsync(cancellationToken);
            var providerIssues = SettingsRoutingProbeSupport.BuildProviderIssues(readinessDto);

            // Llama snapshot from the Plane C alias mirror + endpoint cache — never an
            // inline GetInventoryAsync (the old sequential probe that could hang 300 s).
            var llamaUrl = configuration["LlamaCpp:BaseUrl"];
            int loadedAliases = 0;
            int totalAliases = 0;
            List<string> missingArtifacts = [];
            var llamaSource = "None";
            DateTime? llamaObservedUtc = null;
            if (RuntimeConfigurationPlaceholders.HasUsableUrl(llamaUrl)
                && inventoryMirror.TryGet("llama", out var llamaModels, out var source, out var observedUtc))
            {
                llamaSource = source.ToString();
                llamaObservedUtc = observedUtc;
                totalAliases = llamaModels.Count;
                loadedAliases = llamaModels.Count(m => m.IsActive);
                missingArtifacts = llamaModels.Where(m => !m.IsComplete).Select(m => m.ModelRef).ToList();
            }

            var runtimeSnapshot = new LlamaRuntimeSnapshotDto(
                LoadedAliases: loadedAliases,
                TotalAliases: totalAliases,
                MissingArtifactAliases: missingArtifacts,
                InventorySource: llamaSource,
                ObservedUtc: llamaObservedUtc);

            // Presence per routed service so each status line can be stamped "observed HH:MM".
            var presence = ReachabilitySnapshot.BuildPresence(endpointCache, configuration);

            return Results.Ok(new SettingsOverviewDto(
                GeneratedUtc: DateTime.UtcNow,
                ServiceModeReadiness: serviceRollups,
                ChatTargets: chatRollup,
                ProviderIssues: providerIssues,
                LlamaRuntime: runtimeSnapshot,
                Endpoints: presence));
        })
        .WithName("GetSettingsOverview")
        .Produces<SettingsOverviewDto>(StatusCodes.Status200OK);
    }
}
