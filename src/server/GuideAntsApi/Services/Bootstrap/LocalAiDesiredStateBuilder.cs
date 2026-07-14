using System.Text;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Options;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Bootstrap;

public sealed class WarmupDesiredBuildOptions
{
    public IReadOnlyDictionary<string, string>? ServiceDesiredOverrides { get; init; }

    public string? LlamaRouterAliasOverride { get; init; }

    /// <summary>When true, all auxiliary services are written as off regardless of routing.</summary>
    public bool ForceAuxiliaryIdle { get; init; }
}

public interface ILocalAiDesiredStateBuilder
{
    Task<string> BuildIniAsync(
        WarmupDesiredBuildOptions? options = null,
        CancellationToken cancellationToken = default);
}

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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LocalAiDesiredStateBuilder> _logger;

    public LocalAiDesiredStateBuilder(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IServiceModeResolver serviceModeResolver,
        IHttpClientFactory httpClientFactory,
        ILogger<LocalAiDesiredStateBuilder> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _serviceModeResolver = serviceModeResolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> BuildIniAsync(
        WarmupDesiredBuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WarmupDesiredBuildOptions();
        var builder = new StringBuilder();
        builder.AppendLine("version = 1");
        builder.AppendLine("revision = 0");
        builder.AppendLine($"updated_at_utc = {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        builder.AppendLine();

        await AppendLlamaSectionAsync(builder, options, cancellationToken).ConfigureAwait(false);

        foreach (var serviceId in AuxiliaryServices)
        {
            await AppendAuxiliarySectionAsync(builder, serviceId, options, cancellationToken)
                .ConfigureAwait(false);
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private async Task AppendLlamaSectionAsync(
        StringBuilder builder,
        WarmupDesiredBuildOptions options,
        CancellationToken cancellationToken)
    {
        builder.AppendLine("[llama]");

        var aliasOverride = options.LlamaRouterAliasOverride?.Trim();
        var alias = !string.IsNullOrWhiteSpace(aliasOverride)
            ? aliasOverride
            : await ResolveConfiguredDefaultRouterAliasAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(alias))
        {
            builder.AppendLine("enabled = off");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"router_alias = {alias}");
        builder.AppendLine();
    }

    private async Task AppendAuxiliarySectionAsync(
        StringBuilder builder,
        string serviceId,
        WarmupDesiredBuildOptions options,
        CancellationToken cancellationToken)
    {
        builder.AppendLine($"[{serviceId}]");

        var persistedLocalRef = await ResolvePersistedLocalModeModelRefAsync(serviceId, cancellationToken)
            .ConfigureAwait(false);

        if (options.ServiceDesiredOverrides is not null
            && options.ServiceDesiredOverrides.TryGetValue(serviceId, out var desiredOverride)
            && !string.IsNullOrWhiteSpace(desiredOverride))
        {
            var normalized = desiredOverride.Trim().ToLowerInvariant();
            if (normalized is "warm" or "on")
            {
                AppendAuxiliaryExecutionPlan(
                    builder,
                    serviceId,
                    loadRef: persistedLocalRef,
                    routingWarm: true);
            }
            else
            {
                AppendAuxiliaryExecutionPlan(
                    builder,
                    serviceId,
                    loadRef: persistedLocalRef,
                    routingWarm: false);
            }

            builder.AppendLine();
            return;
        }

        var routingWarm = !options.ForceAuxiliaryIdle
            && await IsLocalRoutingWarmAsync(serviceId, cancellationToken).ConfigureAwait(false);

        AppendAuxiliaryExecutionPlan(
            builder,
            serviceId,
            loadRef: persistedLocalRef,
            routingWarm: routingWarm);
        builder.AppendLine();
    }

    private static void AppendAuxiliaryExecutionPlan(
        StringBuilder builder,
        string serviceId,
        string? loadRef,
        bool routingWarm)
    {
        if (routingWarm && !string.IsNullOrWhiteSpace(loadRef))
        {
            AppendAuxiliaryLoadRef(builder, serviceId, loadRef);
            return;
        }

        builder.AppendLine("enabled = off");
        if (!string.IsNullOrWhiteSpace(loadRef))
        {
            AppendAuxiliaryLoadRef(builder, serviceId, loadRef);
        }
    }

    private static void AppendAuxiliaryLoadRef(StringBuilder builder, string serviceId, string? modelRef)
    {
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            return;
        }

        if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal))
        {
            builder.AppendLine($"bundle_id = {modelRef}");
        }
        else
        {
            builder.AppendLine($"model_path = {modelRef}");
        }
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

        try
        {
            var modes = await _serviceModeResolver
                .GetModesAsync(serviceId, cancellationToken)
                .ConfigureAwait(false);
            var localMode = modes.FirstOrDefault(mode =>
                string.Equals(mode.ProviderSection, localProviderSection, StringComparison.OrdinalIgnoreCase));
            return localMode?.ModelId?.Trim() is { Length: > 0 } modelId
                ? modelId
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed resolving persisted local ServiceModes ModelId for service '{ServiceId}'.",
                serviceId);
            return null;
        }
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

    private async Task<string?> ResolveConfiguredDefaultRouterAliasAsync(CancellationToken cancellationToken)
    {
        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(_configuration["LlamaCpp:BaseUrl"]))
        {
            return null;
        }

        var defaultModelId = (_configuration["ChatDefaults:DefaultModelId"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(defaultModelId))
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
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

        try
        {
            return LocalRuntimeConfigurationParser.ParseRequired(defaultModelId, row.RuntimeConfigJson)
                .RouterModelId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Default llama model '{ModelId}' has invalid RuntimeConfigJson.", defaultModelId);
            return null;
        }
    }

    private enum LocalRoutingDesiredState
    {
        Warm,
        Idle,
        Unknown,
    }

    private async Task<LocalRoutingDesiredState> ResolveLocalRoutingDesiredStateAsync(
        string serviceId,
        CancellationToken cancellationToken)
    {
        var expectedLocalProviderSection = ResolveLocalProviderSection(serviceId);

        if (expectedLocalProviderSection is null)
        {
            return LocalRoutingDesiredState.Warm;
        }

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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve routing mode for {ServiceId}.", serviceId);
            return LocalRoutingDesiredState.Unknown;
        }
    }
}
