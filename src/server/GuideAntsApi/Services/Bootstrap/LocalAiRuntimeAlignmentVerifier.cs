using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Configuration;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Options;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// GuideAntsApi-owned post-apply verification. ga-admin revision/noop is NOT sufficient —
/// engine processes can outlive executor status files (e.g. SD still loaded under cloud routing).
/// DO NOT move this logic into ga-admin warmup_orchestrator.py.
/// </summary>
public interface ILocalAiRuntimeAlignmentVerifier
{
    Task<IReadOnlyList<LocalAiRuntimeAlignmentMismatch>> FindMismatchesAsync(
        string planJson,
        CancellationToken cancellationToken = default);
}

public sealed record LocalAiRuntimeAlignmentMismatch(string ServiceId, string Detail);

public sealed class LocalAiRuntimeAlignmentVerifier : ILocalAiRuntimeAlignmentVerifier
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILocalAiStackHostResolver _stackHostResolver;
    private readonly ILlamaServerRuntimeClient _llamaRuntimeClient;
    private readonly ILogger<LocalAiRuntimeAlignmentVerifier> _logger;

    public LocalAiRuntimeAlignmentVerifier(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalAiStackHostResolver stackHostResolver,
        ILlamaServerRuntimeClient llamaRuntimeClient,
        ILogger<LocalAiRuntimeAlignmentVerifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _stackHostResolver = stackHostResolver;
        _llamaRuntimeClient = llamaRuntimeClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LocalAiRuntimeAlignmentMismatch>> FindMismatchesAsync(
        string planJson,
        CancellationToken cancellationToken = default)
    {
        var mismatches = new List<LocalAiRuntimeAlignmentMismatch>();
        try
        {
            var root = JsonNode.Parse(planJson)?.AsObject();
            if (root is null)
            {
                return [new LocalAiRuntimeAlignmentMismatch("plan", "plan JSON was empty")];
            }

            var services = root["services"]?.AsObject();
            if (services is null)
            {
                return [new LocalAiRuntimeAlignmentMismatch("plan", "plan missing services object")];
            }

            foreach (var serviceId in LocalAiStackHostUrls.WarmupServiceIds)
            {
                if (_stackHostResolver.GetStackBaseForService(serviceId) is null)
                {
                    continue;
                }

                var section = services[serviceId]?.AsObject();
                if (section is null)
                {
                    continue;
                }

                var enabled = section["enabled"]?.GetValue<bool?>() ?? false;
                var planRef = ResolvePlanRef(serviceId, section);
                var shouldLoad = enabled && !string.IsNullOrWhiteSpace(planRef);

                if (string.Equals(serviceId, LocalAiStackHostUrls.LlamaServiceId, StringComparison.Ordinal))
                {
                    mismatches.AddRange(await VerifyLlamaAsync(shouldLoad, planRef, cancellationToken).ConfigureAwait(false));
                    continue;
                }

                mismatches.AddRange(
                    await VerifyAuxiliaryAsync(serviceId, shouldLoad, planRef, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Runtime alignment verification failed to parse or probe engines.");
            mismatches.Add(new LocalAiRuntimeAlignmentMismatch("verification", ex.Message));
        }

        return mismatches;
    }

    private async Task<IReadOnlyList<LocalAiRuntimeAlignmentMismatch>> VerifyLlamaAsync(
        bool shouldLoad,
        string? planRef,
        CancellationToken cancellationToken)
    {
        LlamaModelsResponse models;
        try
        {
            models = await _llamaRuntimeClient.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return [new LocalAiRuntimeAlignmentMismatch(
                LocalAiStackHostUrls.LlamaServiceId,
                $"could not query llama models: {ex.Message}")];
        }

        var loaded = models.Data
            .Where(IsRouterModelLoaded)
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (shouldLoad)
        {
            if (string.IsNullOrWhiteSpace(planRef))
            {
                return [new LocalAiRuntimeAlignmentMismatch(
                    LocalAiStackHostUrls.LlamaServiceId,
                    "plan enabled but router alias missing")];
            }

            if (!loaded.Any(id => string.Equals(id, planRef, StringComparison.Ordinal)))
            {
                return [new LocalAiRuntimeAlignmentMismatch(
                    LocalAiStackHostUrls.LlamaServiceId,
                    $"expected loaded alias '{planRef}' but engine reports [{string.Join(", ", loaded)}]")];
            }

            return Array.Empty<LocalAiRuntimeAlignmentMismatch>();
        }

        if (loaded.Count > 0)
        {
            return [new LocalAiRuntimeAlignmentMismatch(
                LocalAiStackHostUrls.LlamaServiceId,
                $"plan idle but engine still has loaded aliases [{string.Join(", ", loaded)}]")];
        }

        return Array.Empty<LocalAiRuntimeAlignmentMismatch>();
    }

    private async Task<IReadOnlyList<LocalAiRuntimeAlignmentMismatch>> VerifyAuxiliaryAsync(
        string serviceId,
        bool shouldLoad,
        string? planRef,
        CancellationToken cancellationToken)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return Array.Empty<LocalAiRuntimeAlignmentMismatch>();
        }

        var client = _httpClientFactory.CreateClient(LocalAiWarmupOrchestrationClient.HttpClientName);
        if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal))
        {
            return await VerifyImageGenerationAsync(client, adminBase, shouldLoad, planRef, cancellationToken)
                .ConfigureAwait(false);
        }

        return await VerifyReadyEndpointAsync(client, adminBase, serviceId, shouldLoad, planRef, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<LocalAiRuntimeAlignmentMismatch>> VerifyImageGenerationAsync(
        HttpClient client,
        string adminBase,
        bool shouldLoad,
        string? planRef,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{adminBase.TrimEnd('/')}/health", cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (!shouldLoad)
            {
                return Array.Empty<LocalAiRuntimeAlignmentMismatch>();
            }

            return [new LocalAiRuntimeAlignmentMismatch(
                RoutedServiceNames.ImageGeneration,
                $"plan warm but /health returned {(int)response.StatusCode}")];
        }

        try
        {
            var node = JsonNode.Parse(json);
            var engine = node?["engine"] as JsonObject;
            var processAlive = engine?["processAlive"]?.GetValue<bool?>() ?? false;
            var healthy = engine?["healthy"]?.GetValue<bool?>() ?? false;
            var loadedBundle = engine?["loadedBundleId"]?.GetValue<string>()
                ?? node?["loadedBundleId"]?.GetValue<string>();
            var loaded = processAlive && healthy
                || string.Equals(node?["status"]?.GetValue<string>(), "ok", StringComparison.OrdinalIgnoreCase);

            if (shouldLoad)
            {
                if (!loaded)
                {
                    return [new LocalAiRuntimeAlignmentMismatch(
                        RoutedServiceNames.ImageGeneration,
                        "plan warm but SD engine is not loaded")];
                }

                if (!string.IsNullOrWhiteSpace(planRef)
                    && !string.IsNullOrWhiteSpace(loadedBundle)
                    && !string.Equals(loadedBundle.Trim(), planRef.Trim(), StringComparison.Ordinal))
                {
                    return [new LocalAiRuntimeAlignmentMismatch(
                        RoutedServiceNames.ImageGeneration,
                        $"plan bundle '{planRef}' but engine reports '{loadedBundle}'")];
                }

                return Array.Empty<LocalAiRuntimeAlignmentMismatch>();
            }

            if (loaded || processAlive)
            {
                return [new LocalAiRuntimeAlignmentMismatch(
                    RoutedServiceNames.ImageGeneration,
                    $"plan idle but SD engine still active (bundle={loadedBundle ?? "none"})")];
            }

            return Array.Empty<LocalAiRuntimeAlignmentMismatch>();
        }
        catch (JsonException ex)
        {
            return [new LocalAiRuntimeAlignmentMismatch(
                RoutedServiceNames.ImageGeneration,
                $"failed to parse /health: {ex.Message}")];
        }
    }

    private static async Task<IReadOnlyList<LocalAiRuntimeAlignmentMismatch>> VerifyReadyEndpointAsync(
        HttpClient client,
        string adminBase,
        string serviceId,
        bool shouldLoad,
        string? planRef,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{adminBase.TrimEnd('/')}/ready", cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var loaded = false;
        string? loadedRef = null;

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var node = JsonNode.Parse(json);
                loaded = node?["loaded"]?.GetValue<bool?>() ?? false;
                loadedRef = node?["modelRef"]?.GetValue<string>()
                    ?? node?["model_ref"]?.GetValue<string>()
                    ?? node?["catalogEntryId"]?.GetValue<string>()
                    ?? node?["catalog_entry_id"]?.GetValue<string>()
                    ?? node?["bundleId"]?.GetValue<string>()
                    ?? node?["bundle_id"]?.GetValue<string>();
            }
            catch (JsonException)
            {
                loaded = true;
            }
        }

        if (shouldLoad)
        {
            if (!loaded)
            {
                return [new LocalAiRuntimeAlignmentMismatch(serviceId, "plan warm but engine /ready reports not loaded")];
            }

            if (!string.IsNullOrWhiteSpace(planRef)
                && !string.IsNullOrWhiteSpace(loadedRef)
                && !string.Equals(loadedRef.Trim(), planRef.Trim(), StringComparison.Ordinal))
            {
                return [new LocalAiRuntimeAlignmentMismatch(
                    serviceId,
                    $"plan ref '{planRef}' but engine reports '{loadedRef}'")];
            }

            return Array.Empty<LocalAiRuntimeAlignmentMismatch>();
        }

        if (loaded)
        {
            return [new LocalAiRuntimeAlignmentMismatch(
                serviceId,
                $"plan idle but engine still loaded (ref={loadedRef ?? "unknown"})")];
        }

        return Array.Empty<LocalAiRuntimeAlignmentMismatch>();
    }

    private static string? ResolvePlanRef(string serviceId, JsonObject section)
    {
        if (string.Equals(serviceId, LocalAiStackHostUrls.LlamaServiceId, StringComparison.Ordinal))
        {
            return TrimOrNull(section["routerAlias"]?.GetValue<string>());
        }

        if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal))
        {
            return TrimOrNull(section["bundleId"]?.GetValue<string>());
        }

        return TrimOrNull(section["modelPath"]?.GetValue<string>())
            ?? TrimOrNull(section["modelId"]?.GetValue<string>());
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsRouterModelLoaded(LlamaModelData model)
    {
        if (!string.IsNullOrWhiteSpace(model.Status?.Value))
        {
            return string.Equals(model.Status.Value, "loaded", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(model.State))
        {
            return string.Equals(model.State, "loaded", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
