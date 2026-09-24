using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Services.Bootstrap;

public sealed class WarmupDesiredBuildOptions
{
    public IReadOnlyDictionary<string, string>? ServiceDesiredOverrides { get; init; }

    /// <summary>When true, all auxiliary services are written as off regardless of routing.</summary>
    public bool ForceAuxiliaryIdle { get; init; }
}

public interface ILocalAiDesiredStateBuilder
{
    Task<string> BuildPlanJsonAsync(
        WarmupDesiredBuildOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the API-owned lifecycle plan for the auxiliary services
/// (SpeechTranscription, Embeddings, SpeechSynthesis, ImageGeneration), each active
/// on 0 or 1 instance. The plan carries NO llama section: llama actuation is
/// API-direct (IGlobalDefaultLlamaReconciler on global-default change,
/// NotebookModelRuntimeService on use), so a plan apply can never load or unload a
/// llama model.
/// </summary>
public sealed class LocalAiDesiredStateBuilder : ILocalAiDesiredStateBuilder
{
    private static readonly string[] AuxiliaryServices =
    [
        RoutedServiceNames.SpeechTranscription,
        RoutedServiceNames.Embeddings,
        RoutedServiceNames.SpeechSynthesis,
        RoutedServiceNames.ImageGeneration,
    ];

    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceModeResolver _serviceModeResolver;
    private readonly ILogger<LocalAiDesiredStateBuilder> _logger;

    public LocalAiDesiredStateBuilder(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IServiceModeResolver serviceModeResolver,
        ILogger<LocalAiDesiredStateBuilder> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _serviceModeResolver = serviceModeResolver;
        _logger = logger;
    }

    public async Task<string> BuildPlanJsonAsync(
        WarmupDesiredBuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WarmupDesiredBuildOptions();
        var services = new JsonObject();

        foreach (var serviceId in AuxiliaryServices)
        {
            services[serviceId] = await BuildAuxiliarySectionAsync(
                    serviceId,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["services"] = services,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private async Task<JsonObject> BuildAuxiliarySectionAsync(
        string serviceId,
        WarmupDesiredBuildOptions options,
        CancellationToken cancellationToken)
    {
        var persistedLocalRef = await ResolvePersistedLocalModeModelRefAsync(serviceId, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(persistedLocalRef))
        {
            var definition = await GetImageGenerationBundleDefinitionAsync(persistedLocalRef, cancellationToken)
                .ConfigureAwait(false);
            if (definition is null)
            {
                throw new InvalidOperationException(
                    $"ImageGeneration bundle '{persistedLocalRef}' is not defined in API-owned bundle settings.");
            }
        }

        if (options.ServiceDesiredOverrides is not null
            && options.ServiceDesiredOverrides.TryGetValue(serviceId, out var desiredOverride)
            && !string.IsNullOrWhiteSpace(desiredOverride))
        {
            var normalized = desiredOverride.Trim().ToLowerInvariant();
            if (normalized is "warm" or "on")
            {
                return BuildAuxiliaryExecutionPlan(
                    serviceId,
                    loadRef: persistedLocalRef,
                    routingWarm: true);
            }

            return BuildAuxiliaryExecutionPlan(
                serviceId,
                loadRef: persistedLocalRef,
                routingWarm: false);
        }

        var routingWarm = !options.ForceAuxiliaryIdle
            && await IsLocalRoutingWarmAsync(serviceId, cancellationToken).ConfigureAwait(false);

        return BuildAuxiliaryExecutionPlan(
            serviceId,
            loadRef: persistedLocalRef,
            routingWarm: routingWarm);
    }

    private async Task<ImageGenerationBundleDefinitionDto?> GetImageGenerationBundleDefinitionAsync(
        string bundleId,
        CancellationToken cancellationToken)
    {
        var normalizedBundleId = ImageGenerationBundleDefinitionContracts.NormalizeBundleId(bundleId);
        using var scope = _scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
        return await settingsService
            .GetImageGenerationBundleDefinitionAsync(normalizedBundleId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static JsonObject BuildAuxiliaryExecutionPlan(
        string serviceId,
        string? loadRef,
        bool routingWarm)
    {
        if (routingWarm && string.IsNullOrWhiteSpace(loadRef))
        {
            throw new InvalidOperationException(
                $"Service '{serviceId}' is routed to the local provider but has no model or bundle "
                + "configured in ServiceModes. Select an active local model or bundle before warming.");
        }

        if (routingWarm && !string.IsNullOrWhiteSpace(loadRef))
        {
            return BuildAuxiliarySection(serviceId, loadRef, enabled: true);
        }

        return BuildAuxiliarySection(serviceId, loadRef, enabled: false);
    }

    private static JsonObject BuildAuxiliarySection(string serviceId, string? modelRef, bool enabled)
    {
        var section = new JsonObject { ["enabled"] = enabled };
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            return section;
        }

        if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal))
        {
            section["bundleId"] = modelRef;
        }
        else
        {
            section["modelPath"] = modelRef;
        }

        return section;
    }

    private async Task<string?> ResolvePersistedLocalModeModelRefAsync(
        string serviceId,
        CancellationToken cancellationToken)
    {
        var localProviderSection = ResolveLocalProviderSection(serviceId);
        if (localProviderSection is null)
        {
            return null;
        }

        var modes = await _serviceModeResolver
            .GetModesAsync(serviceId, cancellationToken)
            .ConfigureAwait(false);
        var localMode = modes.FirstOrDefault(mode =>
            string.Equals(mode.ProviderSection, localProviderSection, StringComparison.OrdinalIgnoreCase));
        return localMode?.ModelId?.Trim() is { Length: > 0 } modelId
            ? string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal)
                ? ImageGenerationBundleDefinitionContracts.NormalizeBundleId(modelId)
                : modelId
            : null;
    }

    private async Task<bool> IsLocalRoutingWarmAsync(
        string serviceId,
        CancellationToken cancellationToken) =>
        await ResolveLocalRoutingDesiredStateAsync(serviceId, cancellationToken).ConfigureAwait(false)
        == LocalRoutingDesiredState.Warm;

    private static string? ResolveLocalProviderSection(string serviceId) =>
        serviceId switch
        {
            RoutedServiceNames.SpeechTranscription => $"{LocalServiceHostsOptions.SectionName}:SpeechTranscriptionBaseUrl",
            RoutedServiceNames.Embeddings => $"{LocalServiceHostsOptions.SectionName}:EmbeddingsBaseUrl",
            RoutedServiceNames.SpeechSynthesis => $"{LocalServiceHostsOptions.SectionName}:SpeechSynthesisBaseUrl",
            RoutedServiceNames.ImageGeneration => $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl",
            _ => null,
        };

    private enum LocalRoutingDesiredState
    {
        Warm,
        Idle,
    }

    private async Task<LocalRoutingDesiredState> ResolveLocalRoutingDesiredStateAsync(
        string serviceId,
        CancellationToken cancellationToken)
    {
        var expectedLocalProviderSection = ResolveLocalProviderSection(serviceId);

        ArgumentNullException.ThrowIfNull(expectedLocalProviderSection);

        try
        {
            var mode = await _serviceModeResolver
                .ResolveAsync(serviceId, modeId: null, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(mode.ProviderSection, expectedLocalProviderSection, StringComparison.Ordinal))
            {
                return LocalRoutingDesiredState.Warm;
            }

            return LocalRoutingDesiredState.Idle;
        }
        catch (RoutingException ex) when (string.Equals(
            ex.Code,
            RoutingErrorCodes.ModeNotFound,
            StringComparison.Ordinal))
        {
            return LocalRoutingDesiredState.Idle;
        }
    }
}
