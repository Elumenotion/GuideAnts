using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.Configuration;
using GuideAntsApi.Options;
using ServiceProviderIds = GuideAntsApi.Options.ServiceProviderIds;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Bootstrap;

public interface ILocalServiceAutoSelector
{
    Task AutoSelectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs at startup after bootstrap/seeding. For services where the cloud
/// provider connection is not configured but the local container is reachable,
/// automatically activates the local provider so the service is immediately usable.
/// </summary>
public sealed class LocalServiceAutoSelector : ILocalServiceAutoSelector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private static readonly (string ServiceId, string LocalProviderId, string BaseUrlConfigKey)[] LocalAuxiliaryServices =
    [
        (ImageGenerationOptions.SectionName, ServiceProviderIds.ImageGenerationLocalSdHttp, "LocalServiceHosts:ImageGenerationBaseUrl"),
        (EmbeddingsOptions.SectionName, ServiceProviderIds.EmbeddingsLocalEmbHttp, "LocalServiceHosts:EmbeddingsBaseUrl"),
        (SpeechTranscriptionOptions.SectionName, ServiceProviderIds.SpeechTranscriptionLocalAsrHttp, "LocalServiceHosts:SpeechTranscriptionBaseUrl"),
        (SpeechSynthesisOptions.SectionName, ServiceProviderIds.SpeechSynthesisLocalTtsHttp, "LocalServiceHosts:SpeechSynthesisBaseUrl"),
    ];

    private readonly IApplicationSettingsService _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalServiceAutoSelector> _logger;

    public LocalServiceAutoSelector(
        IApplicationSettingsService settings,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LocalServiceAutoSelector> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task AutoSelectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (serviceId, localProviderId, baseUrlConfigKey) in LocalAuxiliaryServices)
        {
            await TryAutoSelectLocalProviderAsync(
                    serviceId,
                    localProviderId,
                    baseUrlConfigKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await TryAutoSelectDoclingAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryAutoSelectLocalProviderAsync(
        string serviceId,
        string localProviderId,
        string baseUrlConfigKey,
        CancellationToken cancellationToken)
    {
        var state = await _settings.GetServiceEditorStateAsync(serviceId, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(state.ActiveProviderId)
            && string.Equals(state.ActiveProviderId, localProviderId, StringComparison.Ordinal))
        {
            return;
        }

        if (state.Providers.Any(provider =>
                string.Equals(provider.ProviderKind, "Cloud", StringComparison.OrdinalIgnoreCase)
                && provider.ConnectionConfigured))
        {
            return;
        }

        var localBaseUrl = _configuration[baseUrlConfigKey];
        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(localBaseUrl))
        {
            _logger.LogInformation(
                "{ConfigKey} is not configured with a usable URL; skipping local auto-select for {ServiceId}",
                baseUrlConfigKey,
                serviceId);
            return;
        }

        localBaseUrl = localBaseUrl!.Trim();
        if (!await IsReachableAsync(localBaseUrl, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Local service for {ServiceId} at {Url} is not reachable; skipping auto-select",
                serviceId,
                localBaseUrl);
            return;
        }

        try
        {
            await _settings.EnsureServiceModeExistsAsync(serviceId, localProviderId, cancellationToken)
                .ConfigureAwait(false);
            await _settings.SetServiceActiveProviderAsync(serviceId, localProviderId, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Auto-selected {LocalProviderId} as {ServiceId} provider (no cloud connection configured, local service reachable at {Url})",
                localProviderId,
                serviceId,
                localBaseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to auto-select {LocalProviderId} as {ServiceId} provider",
                localProviderId,
                serviceId);
        }
    }

    private async Task TryAutoSelectDoclingAsync(CancellationToken cancellationToken)
    {
        await TryAutoSelectLocalProviderAsync(
                GuideAntsApi.BackgroundJobs.Options.DocumentIntelligenceOptions.SectionName,
                ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp,
                "LocalServiceHosts:DocumentIntelligenceBaseUrl",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> IsReachableAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(ProbeTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);
            return (int)response.StatusCode is >= 100 and < 500;
        }
        catch
        {
            return false;
        }
    }
}
