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

    /// <summary>
    /// The router alias a load operation requested. It is applied to the instance the
    /// alias's row declares (its stackBaseUrl), falling back to the default machine when
    /// the row declares no row-owned stack.
    /// </summary>
    public string? LlamaRouterAliasOverride { get; init; }

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
/// Builds the API-owned lifecycle plan. Llama is per-instance: the plan carries one
/// section per configured instance (global LlamaCpp:BaseUrl + row-owned stacks), each
/// with the singular alias that instance must hold. ga-admin applies sections per
/// instance; the splitter fans the plan out, and every instance's executor reconciles
/// its own llama server toward its own section (unload-then-load, within the instance).
/// Non-llama services remain single-instance (active on 0 or 1 instance).
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
    private readonly INotebookChatAliasState _notebookChatAliasState;
    private readonly ILocalAiStackHostResolver _stackHostResolver;
    private readonly ILogger<LocalAiDesiredStateBuilder> _logger;

    public LocalAiDesiredStateBuilder(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IServiceModeResolver serviceModeResolver,
        INotebookChatAliasState notebookChatAliasState,
        ILocalAiStackHostResolver stackHostResolver,
        ILogger<LocalAiDesiredStateBuilder> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _serviceModeResolver = serviceModeResolver;
        _notebookChatAliasState = notebookChatAliasState;
        _stackHostResolver = stackHostResolver;
        _logger = logger;
    }

    public async Task<string> BuildPlanJsonAsync(
        WarmupDesiredBuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WarmupDesiredBuildOptions();
        var services = new JsonObject();

        var llamaSections = await BuildLlamaSectionsAsync(options, cancellationToken).ConfigureAwait(false);
        foreach (var (canonicalKey, section) in llamaSections)
        {
            services[$"{LocalAiStackHostUrls.LlamaServiceId}.{canonicalKey}"] = section;
        }

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

    /// <summary>
    /// One section per configured instance. The section's alias is, in priority order:
    /// a row-owned alias (a row on this instance) &gt; the global chat-default alias
    /// (only for the global instance) &gt; a notebook alias recorded for this instance
    /// &gt; disabled. Disabled is the only unload source, and it is applied only by the
    /// normal plan triggers (chat-defaults change, notebook unload, warmup apply).
    /// </summary>
    private async Task<IReadOnlyList<(string CanonicalKey, JsonObject Section)>> BuildLlamaSectionsAsync(
        WarmupDesiredBuildOptions options,
        CancellationToken cancellationToken)
    {
        var globalBase = _stackHostResolver.GetStackBaseForService(LocalAiStackHostUrls.LlamaServiceId);
        // One physical box = one instance, even when configured under several host names.
        var instances = await _stackHostResolver.GetAllConfiguredInstancesAsync(cancellationToken).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<ApplicationDbContext>();

        // Row-owned aliases per instance (rule 4: the required model list on the instance).
        var aliasToInstance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (db is not null)
        {
            var rows = await db.Models
                .AsNoTracking()
                .Where(m => m.Provider == "llama-cpp" && m.IsActive && m.RuntimeConfigJson != null)
                .Select(m => new { m.ModelId, m.RuntimeConfigJson })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
                {
                    continue; // Row without a config cannot declare placement; skip rather than fail the whole build.
                }

                LocalRuntimeConfiguration configuration;
                try
                {
                    configuration = LocalRuntimeConfigurationParser.Parse(row.ModelId, row.RuntimeConfigJson);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex,
                        "[DIAG] BuildLlamaSectionsAsync: skipping model '{ModelId}' with invalid RuntimeConfigJson.",
                        row.ModelId);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(configuration.StackBaseUrl)
                    && LocalAiStackHostUrls.NormalizeStackBaseUrl(configuration.StackBaseUrl) is { } normalizedBase)
                {
                    // Name->machine map only: resolves WHERE a model's row says its model
                    // runs. It carries no load directive - a row declaring placement is not
                    // an order to the machine to hold that alias.
                    aliasToInstance[configuration.RouterModelId] =
                        LocalAiStackHostResolver.CanonicalInstanceKey(normalizedBase);
                }
            }
        }

        var defaultInfo = db is not null && globalBase is not null
            ? await ResolveConfiguredDefaultRouterAliasAsync(scope, db, cancellationToken).ConfigureAwait(false)
            : null;
        var defaultAlias = defaultInfo?.Alias;
        var notebookAliases = _notebookChatAliasState.GetActiveChatAliases();

        _logger.LogInformation(
            "[DIAG] BuildLlamaSectionsAsync: db={Db} globalBase={GlobalBase} rowBases={RowBases} defaultAlias={DefaultAlias} notebookAliases={NotebookAliases}",
            db is null ? "NULL" : "present",
            globalBase ?? "null",
            string.Join(",", instances.Select(i => i.CanonicalKey + "(" + i.Base + ")")),
            defaultAlias ?? "null",
            string.Join(";", notebookAliases.Select(kv => kv.Key + "=" + kv.Value)));

        // There is no "global stack": the default is a model, and a model determines
        // the machine that runs it (its row's stackBaseUrl). Each instance's alias comes
        // only from live intent: the requested alias (load path) > the default model's
        // alias > the notebook's active alias for that instance > disabled. A row that
        // declares a model's placement never injects a load demand of its own, so a
        // machine is never told to hold a model nothing has asked for.
        var globalKey = globalBase is not null
            ? LocalAiStackHostResolver.CanonicalInstanceKey(globalBase)
            : null;

        string? overrideKey = null;
        var overrideAlias = options.LlamaRouterAliasOverride?.Trim();
        if (!string.IsNullOrWhiteSpace(overrideAlias))
        {
            overrideKey = aliasToInstance.TryGetValue(overrideAlias!, out var ovKey)
                ? ovKey
                : globalKey;
        }

        var defaultKey = defaultInfo is null
            ? null
            : (defaultInfo.StackBaseUrl is { Length: > 0 } defaultStack
                && LocalAiStackHostUrls.NormalizeStackBaseUrl(defaultStack) is { } defaultNorm
                ? LocalAiStackHostResolver.CanonicalInstanceKey(defaultNorm)
                : globalKey);

        var sections = new List<(string CanonicalKey, JsonObject Section)>(instances.Count);
        foreach (var instance in instances)
        {
            var canonicalKey = instance.CanonicalKey;
            string? alias = null;
            if (overrideKey is not null
                && string.Equals(canonicalKey, overrideKey, StringComparison.OrdinalIgnoreCase))
            {
                alias = overrideAlias;
            }
            else if (defaultKey is not null
                && string.Equals(canonicalKey, defaultKey, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(defaultAlias))
            {
                alias = defaultAlias.Trim();
            }
            else if (notebookAliases.TryGetValue(canonicalKey, out var notebookAlias))
            {
                alias = notebookAlias;
            }

            sections.Add((canonicalKey, BuildLlamaSection(alias)));
        }
        return sections;
    }

    private static JsonObject BuildLlamaSection(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return new JsonObject { ["enabled"] = false };
        }

        return new JsonObject
        {
            ["enabled"] = true,
            ["routerAlias"] = alias.Trim(),
        };
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

    private sealed record DefaultRouterAlias(string Alias, string? StackBaseUrl);

    private async Task<DefaultRouterAlias?> ResolveConfiguredDefaultRouterAliasAsync(
        IServiceScope scope,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(_configuration["LlamaCpp:BaseUrl"]))
        {
            return null;
        }

        var settingsService = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
        var chatDefaultsSection = await settingsService
            .GetSectionAsync("ChatDefaults", cancellationToken)
            .ConfigureAwait(false);
        var defaultModelId = ChatDefaultsSnapshot.FromSection(chatDefaultsSection).DefaultModelId?.Trim();
        if (string.IsNullOrWhiteSpace(defaultModelId))
        {
            return null;
        }

        var row = await db.Models
            .AsNoTracking()
            .Where(m => m.ModelId == defaultModelId)
            .Select(m => new { m.ModelId, m.Provider, m.RuntimeConfigJson, m.IsActive })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null
            || !row.IsActive
            || !string.Equals(row.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
        {
            return null;
        }

        var config = LocalRuntimeConfigurationParser.ParseRequired(defaultModelId, row.RuntimeConfigJson);
        return new DefaultRouterAlias(config.RouterModelId, config.StackBaseUrl);
    }

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
