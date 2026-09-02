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
    internal static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ReadinessWaitTimeout = TimeSpan.FromSeconds(30);

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
                var planRefs = ResolvePlanRefs(serviceId, section);
                var shouldLoad = enabled && planRefs.Count > 0;

                if (string.Equals(serviceId, LocalAiStackHostUrls.LlamaServiceId, StringComparison.Ordinal))
                {
                    mismatches.AddRange(
                        await VerifyLlamaWithReadinessWaitAsync(shouldLoad, planRefs, cancellationToken)
                            .ConfigureAwait(false));
                    continue;
                }

                mismatches.AddRange(
                    await VerifyAuxiliaryWithReadinessWaitAsync(serviceId, shouldLoad, planRefs, cancellationToken)
                        .ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Runtime alignment verification failed to parse or probe engines.");
            mismatches.Add(new LocalAiRuntimeAlignmentMismatch("verification", ex.Message));
        }

        return mismatches;
    }

    private async Task<IReadOnlyList<LocalAiRuntimeAlignmentMismatch>> VerifyLlamaWithReadinessWaitAsync(
        bool shouldLoad,
        IReadOnlyList<string> planRefs,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ReadinessWaitTimeout;
        while (true)
        {
            var probe = await ProbeLlamaAsync(shouldLoad, planRefs, cancellationToken).ConfigureAwait(false);
            if (!probe.IsTransientReadiness || DateTime.UtcNow >= deadline)
            {
                return probe.Mismatches;
            }

            _logger.LogDebug(
                "Llama runtime not ready yet ({Detail}); waiting {IntervalSeconds}s before re-check",
                probe.Mismatches[0].Detail,
                ReadinessPollInterval.TotalSeconds);
            await Task.Delay(ReadinessPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(IReadOnlyList<LocalAiRuntimeAlignmentMismatch> Mismatches, bool IsTransientReadiness)> ProbeLlamaAsync(
        bool shouldLoad,
        IReadOnlyList<string> planRefs,
        CancellationToken cancellationToken)
    {
        LlamaModelsResponse models;
        try
        {
            models = await _llamaRuntimeClient.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (shouldLoad && IsTransientTransportFailure(ex))
        {
            return ([new LocalAiRuntimeAlignmentMismatch(
                LocalAiStackHostUrls.LlamaServiceId,
                $"could not query llama models: {ex.Message}")], true);
        }
        catch (Exception ex)
        {
            return ([new LocalAiRuntimeAlignmentMismatch(
                LocalAiStackHostUrls.LlamaServiceId,
                $"could not query llama models: {ex.Message}")], false);
        }

        var loaded = models.Data
            .Where(IsRouterModelLoaded)
            .SelectMany(m => CollectRuntimeIdentifiers(m.Id))
            .ToList();

        if (shouldLoad)
        {
            if (planRefs.Count == 0)
            {
                return ([new LocalAiRuntimeAlignmentMismatch(
                    LocalAiStackHostUrls.LlamaServiceId,
                    "plan enabled but router alias missing")], false);
            }

            if (!IdentifiersMatch(planRefs, loaded))
            {
                if (loaded.Count == 0)
                {
                    return ([new LocalAiRuntimeAlignmentMismatch(
                        LocalAiStackHostUrls.LlamaServiceId,
                        $"expected loaded alias '{planRefs[0]}' but engine reports no loaded aliases")], true);
                }

                return ([new LocalAiRuntimeAlignmentMismatch(
                    LocalAiStackHostUrls.LlamaServiceId,
                    $"expected loaded alias '{planRefs[0]}' but engine reports [{string.Join(", ", loaded)}]")], false);
            }

            return (Array.Empty<LocalAiRuntimeAlignmentMismatch>(), false);
        }

        if (loaded.Count > 0)
        {
            return ([new LocalAiRuntimeAlignmentMismatch(
                LocalAiStackHostUrls.LlamaServiceId,
                $"plan idle but engine still has loaded aliases [{string.Join(", ", loaded)}]")], false);
        }

        return (Array.Empty<LocalAiRuntimeAlignmentMismatch>(), false);
    }

    private async Task<IReadOnlyList<LocalAiRuntimeAlignmentMismatch>> VerifyAuxiliaryWithReadinessWaitAsync(
        string serviceId,
        bool shouldLoad,
        IReadOnlyList<string> planRefs,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ReadinessWaitTimeout;
        while (true)
        {
            var probe = await ProbeAuxiliaryAsync(serviceId, shouldLoad, planRefs, cancellationToken)
                .ConfigureAwait(false);
            if (!probe.IsTransientReadiness || DateTime.UtcNow >= deadline)
            {
                return probe.Mismatches;
            }

            _logger.LogDebug(
                "Auxiliary runtime {ServiceId} not ready yet ({Detail}); waiting {IntervalSeconds}s before re-check",
                serviceId,
                probe.Mismatches[0].Detail,
                ReadinessPollInterval.TotalSeconds);
            await Task.Delay(ReadinessPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(IReadOnlyList<LocalAiRuntimeAlignmentMismatch> Mismatches, bool IsTransientReadiness)> ProbeAuxiliaryAsync(
        string serviceId,
        bool shouldLoad,
        IReadOnlyList<string> planRefs,
        CancellationToken cancellationToken)
    {
        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, _configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return (Array.Empty<LocalAiRuntimeAlignmentMismatch>(), false);
        }

        var client = _httpClientFactory.CreateClient(LocalAiWarmupOrchestrationClient.HttpClientName);
        if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal))
        {
            return await ProbeImageGenerationAsync(client, adminBase, shouldLoad, planRefs, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ProbeReadyEndpointAsync(client, adminBase, serviceId, shouldLoad, planRefs, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<(IReadOnlyList<LocalAiRuntimeAlignmentMismatch> Mismatches, bool IsTransientReadiness)>
        ProbeImageGenerationAsync(
            HttpClient client,
            string adminBase,
            bool shouldLoad,
            IReadOnlyList<string> planRefs,
            CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{adminBase.TrimEnd('/')}/health", cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (!shouldLoad)
            {
                return (Array.Empty<LocalAiRuntimeAlignmentMismatch>(), false);
            }

            if ((int)response.StatusCode == 503)
            {
                return ([new LocalAiRuntimeAlignmentMismatch(
                    RoutedServiceNames.ImageGeneration,
                    $"plan warm but /health returned {(int)response.StatusCode}")], true);
            }

            return ([new LocalAiRuntimeAlignmentMismatch(
                RoutedServiceNames.ImageGeneration,
                $"plan warm but /health returned {(int)response.StatusCode}")], false);
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
            var runtimeRefs = CollectRuntimeIdentifiers(loadedBundle)
                .Concat(CollectRuntimeIdentifiers(node))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (shouldLoad)
            {
                if (!loaded)
                {
                    if (IsWarmupIncomplete(node, failed: false, failedReason: null, statusCode: (int)response.StatusCode))
                    {
                        return ([new LocalAiRuntimeAlignmentMismatch(
                            RoutedServiceNames.ImageGeneration,
                            "plan warm but SD engine is warming up")], true);
                    }

                    return ([new LocalAiRuntimeAlignmentMismatch(
                        RoutedServiceNames.ImageGeneration,
                        "plan warm but SD engine is not loaded")], false);
                }

                if (planRefs.Count > 0
                    && runtimeRefs.Count > 0
                    && !IdentifiersMatch(planRefs, runtimeRefs))
                {
                    return ([new LocalAiRuntimeAlignmentMismatch(
                        RoutedServiceNames.ImageGeneration,
                        $"plan bundle '{planRefs[0]}' but engine reports '{runtimeRefs[0]}'")], false);
                }

                return (Array.Empty<LocalAiRuntimeAlignmentMismatch>(), false);
            }

            if (loaded || processAlive)
            {
                return ([new LocalAiRuntimeAlignmentMismatch(
                    RoutedServiceNames.ImageGeneration,
                    $"plan idle but SD engine still active (bundle={loadedBundle ?? "none"})")], false);
            }

            return (Array.Empty<LocalAiRuntimeAlignmentMismatch>(), false);
        }
        catch (JsonException ex)
        {
            return ([new LocalAiRuntimeAlignmentMismatch(
                RoutedServiceNames.ImageGeneration,
                $"failed to parse /health: {ex.Message}")], false);
        }
    }

    private static async Task<(IReadOnlyList<LocalAiRuntimeAlignmentMismatch> Mismatches, bool IsTransientReadiness)>
        ProbeReadyEndpointAsync(
            HttpClient client,
            string adminBase,
            string serviceId,
            bool shouldLoad,
            IReadOnlyList<string> planRefs,
            CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{adminBase.TrimEnd('/')}/ready", cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var loaded = false;
        var failed = false;
        string? failedReason = null;
        JsonNode? node = null;

        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            node = null;
        }

        if (node is not null)
        {
            loaded = node["loaded"]?.GetValue<bool?>() ?? false;
            failed = node["failed"]?.GetValue<bool?>() ?? false;
            failedReason = node["failedReason"]?.GetValue<string>()
                ?? node["message"]?.GetValue<string>();
        }
        else if (response.IsSuccessStatusCode)
        {
            loaded = true;
        }

        var runtimeRefs = CollectRuntimeIdentifiers(node);

        if (shouldLoad)
        {
            if (IsWarmupIncomplete(node, failed, failedReason, (int)response.StatusCode))
            {
                return ([new LocalAiRuntimeAlignmentMismatch(serviceId, "engine warmup incomplete")], true);
            }

            if (failed || (int)response.StatusCode == 503)
            {
                var reason = string.IsNullOrWhiteSpace(failedReason)
                    ? "engine is failed/unready"
                    : $"engine is failed/unready: {failedReason}";
                return ([new LocalAiRuntimeAlignmentMismatch(serviceId, reason)], true);
            }

            if (!response.IsSuccessStatusCode || !loaded)
            {
                return ([new LocalAiRuntimeAlignmentMismatch(serviceId, "plan warm but engine /ready reports not loaded")], true);
            }

            if (planRefs.Count > 0
                && runtimeRefs.Count > 0
                && !IdentifiersMatch(planRefs, runtimeRefs))
            {
                return ([new LocalAiRuntimeAlignmentMismatch(
                    serviceId,
                    $"plan ref '{planRefs[0]}' but engine reports '{runtimeRefs[0]}'")], false);
            }

            return (Array.Empty<LocalAiRuntimeAlignmentMismatch>(), false);
        }

        if (loaded)
        {
            return ([new LocalAiRuntimeAlignmentMismatch(
                serviceId,
                $"plan idle but engine still loaded (ref={runtimeRefs.FirstOrDefault() ?? "unknown"})")], false);
        }

        return (Array.Empty<LocalAiRuntimeAlignmentMismatch>(), false);
    }

    internal static IReadOnlyList<string> ResolvePlanRefs(string serviceId, JsonObject section)
    {
        if (string.Equals(serviceId, LocalAiStackHostUrls.LlamaServiceId, StringComparison.Ordinal))
        {
            return CollectRuntimeIdentifiers(section["routerAlias"]?.GetValue<string>());
        }

        if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal))
        {
            return CollectRuntimeIdentifiers(section["bundleId"]?.GetValue<string>());
        }

        return CollectRuntimeIdentifiers(
            section["modelPath"]?.GetValue<string>(),
            section["modelId"]?.GetValue<string>(),
            section["catalogEntryId"]?.GetValue<string>(),
            section["bundleId"]?.GetValue<string>());
    }

    internal static IReadOnlyList<string> CollectRuntimeIdentifiers(params string?[] values)
    {
        var identifiers = new List<string>();
        foreach (var value in values)
        {
            AddIdentifier(identifiers, value);
        }

        return identifiers;
    }

    internal static IReadOnlyList<string> CollectRuntimeIdentifiers(JsonNode? node)
    {
        if (node is null)
        {
            return Array.Empty<string>();
        }

        if (node is JsonObject obj)
        {
            return CollectRuntimeIdentifiers(
                obj["modelRef"]?.GetValue<string>(),
                obj["model_ref"]?.GetValue<string>(),
                obj["catalogEntryId"]?.GetValue<string>(),
                obj["catalog_entry_id"]?.GetValue<string>(),
                obj["bundleId"]?.GetValue<string>(),
                obj["bundle_id"]?.GetValue<string>(),
                obj["modelPath"]?.GetValue<string>(),
                obj["modelId"]?.GetValue<string>(),
                obj["loadedBundleId"]?.GetValue<string>());
        }

        return CollectRuntimeIdentifiers(node.GetValue<string>());
    }

    internal static bool IdentifiersMatch(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        if (expected.Count == 0 || actual.Count == 0)
        {
            return false;
        }

        foreach (var expectedRef in expected)
        {
            foreach (var actualRef in actual)
            {
                if (RuntimeIdentifiersEqual(expectedRef, actualRef))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool RuntimeIdentifiersEqual(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        if (LooksLikePath(left) || LooksLikePath(right))
        {
            var normalizedLeft = NormalizePath(left);
            var normalizedRight = NormalizePath(right);
            if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var leftFileName = Path.GetFileName(normalizedLeft);
            var rightFileName = Path.GetFileName(normalizedRight);
            if (!string.IsNullOrWhiteSpace(leftFileName)
                && string.Equals(leftFileName, rightFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (SlugifiedIdentifiersEqual(left, leftFileName)
                || SlugifiedIdentifiersEqual(left, right)
                || SlugifiedIdentifiersEqual(right, leftFileName)
                || SlugifiedIdentifiersEqual(right, right))
            {
                return true;
            }
        }

        return SlugifiedIdentifiersEqual(left, right);
    }

    private static bool SlugifiedIdentifiersEqual(string left, string right)
    {
        var normalizedLeft = SlugifyIdentifier(left);
        var normalizedRight = SlugifyIdentifier(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static string SlugifyIdentifier(string value)
    {
        var trimmed = TrimOrNull(value);
        if (trimmed is null)
        {
            return string.Empty;
        }

        var withoutExtension = LooksLikePath(trimmed)
            ? Path.GetFileNameWithoutExtension(trimmed)
            : trimmed;

        return new string(withoutExtension
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static bool IsWarmupIncomplete(JsonNode? node, bool failed, string? failedReason, int statusCode)
    {
        if (statusCode == 503)
        {
            return true;
        }

        var status = node?["status"]?.GetValue<string>();
        if (string.Equals(status, "warmup-incomplete", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (node?["warmupIncomplete"]?.GetValue<bool?>() == true)
        {
            return true;
        }

        if (failed
            && !string.IsNullOrWhiteSpace(failedReason)
            && failedReason.Contains("warmup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsTransientTransportFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;

    private static void AddIdentifier(List<string> identifiers, string? value)
    {
        var trimmed = TrimOrNull(value);
        if (trimmed is null)
        {
            return;
        }

        if (!identifiers.Contains(trimmed, StringComparer.Ordinal))
        {
            identifiers.Add(trimmed);
        }
    }

    private static bool LooksLikePath(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal)
        || value.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string value) =>
        value.Replace('\\', '/').Trim();

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
