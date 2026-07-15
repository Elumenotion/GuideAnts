using System.Text.Json;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Endpoints.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Copies configured local model/bundle selections into ServiceModes before the
/// API builds warmup-desired.ini. SD persists active bundles on disk; ASR/TTS/Emb
/// expose the active folder via /admin/models when configured.
/// </summary>
internal static class ConfiguredLocalServiceSelectionSync
{
    internal static readonly string[] WarmLocalAuxiliaryServices =
    [
        RoutedServiceNames.SpeechTranscription,
        RoutedServiceNames.Embeddings,
        RoutedServiceNames.SpeechSynthesis,
        RoutedServiceNames.ImageGeneration,
    ];

    public static async Task SyncAllWarmLocalServicesAsync(
        IApplicationSettingsService settings,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        Func<string, CancellationToken, Task<bool>> isLocalRoutingWarmAsync,
        CancellationToken cancellationToken)
    {
        foreach (var serviceId in WarmLocalAuxiliaryServices)
        {
            if (!await isLocalRoutingWarmAsync(serviceId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal))
            {
                await SyncImageBundleFromSdAdminAsync(
                        settings,
                        configuration,
                        httpClientFactory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await SyncActiveModelFromAdminAsync(
                        settings,
                        serviceId,
                        configuration,
                        httpClientFactory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public static async Task<string?> ResolveOrSyncImageBundleAsync(
        IApplicationSettingsService settings,
        string? configuredBundleIdFromSd,
        CancellationToken cancellationToken)
    {
        var persisted = await ReadPersistedLocalModelRefAsync(
                settings,
                RoutedServiceNames.ImageGeneration,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(persisted))
        {
            return persisted;
        }

        var configured = configuredBundleIdFromSd?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        await settings
            .SetServiceModeModelIdAsync(RoutedServiceNames.ImageGeneration, configured, cancellationToken)
            .ConfigureAwait(false);
        return configured;
    }

    private static async Task SyncImageBundleFromSdAdminAsync(
        IApplicationSettingsService settings,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(await ReadPersistedLocalModelRefAsync(
                    settings,
                    RoutedServiceNames.ImageGeneration,
                    cancellationToken)
                .ConfigureAwait(false)))
        {
            return;
        }

        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(
            RoutedServiceNames.ImageGeneration,
            configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return;
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var response = await client
                .GetAsync($"{adminBase.TrimEnd('/')}/admin/bundles", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var configured = ServiceLocalModelListEnricher.ReadConfiguredActiveBundleId(body);
            await ResolveOrSyncImageBundleAsync(settings, configured, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Warmup build will fail loudly if routing is warm but nothing was synced.
        }
    }

    private static async Task SyncActiveModelFromAdminAsync(
        IApplicationSettingsService settings,
        string serviceId,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(await ReadPersistedLocalModelRefAsync(settings, serviceId, cancellationToken)
                .ConfigureAwait(false)))
        {
            return;
        }

        var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
        if (string.IsNullOrWhiteSpace(adminBase))
        {
            return;
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var response = await client
                .GetAsync($"{adminBase.TrimEnd('/')}/admin/models", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var configured = ReadActiveModelRefFromModelsBody(body);
            if (string.IsNullOrWhiteSpace(configured))
            {
                return;
            }

            await settings
                .SetServiceModeModelIdAsync(serviceId, configured, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Warmup build will fail loudly if routing is warm but nothing was synced.
        }
    }

    internal static string? ReadActiveModelRefFromModelsBody(string upstreamBody)
    {
        try
        {
            using var document = JsonDocument.Parse(upstreamBody);
            if (!document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("active", out var activeElement)
                    || activeElement.ValueKind != JsonValueKind.True)
                {
                    continue;
                }

                if (item.TryGetProperty("modelRef", out var modelRefElement)
                    && modelRefElement.ValueKind == JsonValueKind.String)
                {
                    var modelRef = modelRefElement.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(modelRef))
                    {
                        return modelRef;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static async Task<string?> ReadPersistedLocalModelRefAsync(
        IApplicationSettingsService settings,
        string serviceId,
        CancellationToken cancellationToken)
    {
        var localSection = ResolveLocalProviderSection(serviceId);
        if (localSection is null)
        {
            return null;
        }

        var modes = await settings.GetServiceModesAsync(serviceId, cancellationToken).ConfigureAwait(false);
        var localMode = modes.FirstOrDefault(mode =>
            string.Equals(mode.ProviderSection, localSection, StringComparison.OrdinalIgnoreCase));
        var selected = localMode?.ModelId?.Trim();
        return string.IsNullOrWhiteSpace(selected) ? null : selected;
    }

    private static string? ResolveLocalProviderSection(string serviceId) =>
        serviceId switch
        {
            RoutedServiceNames.SpeechTranscription => $"{LocalServiceHostsOptions.SectionName}:SpeechTranscriptionBaseUrl",
            RoutedServiceNames.Embeddings => $"{LocalServiceHostsOptions.SectionName}:EmbeddingsBaseUrl",
            RoutedServiceNames.SpeechSynthesis => $"{LocalServiceHostsOptions.SectionName}:SpeechSynthesisBaseUrl",
            RoutedServiceNames.ImageGeneration => $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl",
            _ => null,
        };
}
