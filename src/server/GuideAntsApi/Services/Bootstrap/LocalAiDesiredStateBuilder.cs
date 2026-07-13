using System.Text;
using System.Text.Json;
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

    public IReadOnlyDictionary<string, string>? ModelIdOverrides { get; init; }

    public string? LlamaRouterAliasOverride { get; init; }

    /// <summary>When true, all auxiliary services are written as idle regardless of routing.</summary>
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
            builder.AppendLine("desired = idle");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("desired = warm");
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

        if (options.ForceAuxiliaryIdle)
        {
            builder.AppendLine("desired = idle");
            builder.AppendLine();
            return;
        }

        if (options.ServiceDesiredOverrides is not null
            && options.ServiceDesiredOverrides.TryGetValue(serviceId, out var desiredOverride)
            && !string.IsNullOrWhiteSpace(desiredOverride))
        {
            var normalized = desiredOverride.Trim().ToLowerInvariant();
            builder.AppendLine($"desired = {normalized}");
            if (normalized == "warm")
            {
                var modelRef = await ResolveAuxiliaryModelRefAsync(serviceId, options, cancellationToken)
                    .ConfigureAwait(false);
                AppendAuxiliaryWarmModelRef(builder, serviceId, modelRef);
            }

            builder.AppendLine();
            return;
        }

        var routing = await ResolveLocalRoutingDesiredStateAsync(serviceId, cancellationToken)
            .ConfigureAwait(false);
        if (routing == LocalRoutingDesiredState.Warm)
        {
            var modelRef = await ResolveAuxiliaryModelRefAsync(serviceId, options, cancellationToken)
                .ConfigureAwait(false);
            builder.AppendLine("desired = warm");
            AppendAuxiliaryWarmModelRef(builder, serviceId, modelRef);
        }
        else
        {
            builder.AppendLine("desired = idle");
        }

        builder.AppendLine();
    }

    private static void AppendAuxiliaryWarmModelRef(StringBuilder builder, string serviceId, string? modelRef)
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
            builder.AppendLine($"model_id = {modelRef}");
        }
    }

    private async Task<string?> ResolveAuxiliaryModelRefAsync(
        string serviceId,
        WarmupDesiredBuildOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ModelIdOverrides is not null
            && options.ModelIdOverrides.TryGetValue(serviceId, out var overrideRef)
            && !string.IsNullOrWhiteSpace(overrideRef))
        {
            return overrideRef.Trim();
        }

        if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal))
        {
            return await ResolveImageGenerationBundleRefAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ResolveConfiguredServiceModeModelRefAsync(serviceId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> ResolveImageGenerationBundleRefAsync(CancellationToken cancellationToken)
    {
        var fromMode = await ResolveConfiguredServiceModeModelRefAsync(
                RoutedServiceNames.ImageGeneration,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromMode))
        {
            return fromMode;
        }

        // Local SD marks the active bundle on disk; ServiceModes often has no ModelId.
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(
            RoutedServiceNames.ImageGeneration,
            _configuration);
        if (adminBase is null)
        {
            return null;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var response = await client
                .GetAsync($"{adminBase.TrimEnd('/')}/admin/bundles", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "ImageGeneration bundle inventory returned {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("activeBundleId", out var active)
                && active.ValueKind == JsonValueKind.String)
            {
                var bundleId = active.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(bundleId) ? null : bundleId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed resolving active ImageGeneration bundle from local SD admin.");
        }

        return null;
    }

    private async Task<string?> ResolveConfiguredServiceModeModelRefAsync(
        string serviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var mode = await _serviceModeResolver
                .ResolveAsync(serviceId, modeId: null, cancellationToken)
                .ConfigureAwait(false);
            var modelId = mode.ModelId?.Trim();
            return string.IsNullOrWhiteSpace(modelId) ? null : modelId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed resolving configured ServiceModes ModelId for service '{ServiceId}'.",
                serviceId);
            return null;
        }
    }

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
        var expectedLocalProviderSection = serviceId switch
        {
            RoutedServiceNames.SpeechTranscription => $"{LocalServiceHostsOptions.SectionName}:SpeechTranscriptionBaseUrl",
            RoutedServiceNames.Embeddings => $"{LocalServiceHostsOptions.SectionName}:EmbeddingsBaseUrl",
            RoutedServiceNames.SpeechSynthesis => $"{LocalServiceHostsOptions.SectionName}:SpeechSynthesisBaseUrl",
            RoutedServiceNames.ImageGeneration => $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl",
            _ => null,
        };

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
