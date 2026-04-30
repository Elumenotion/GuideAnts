using GuideAntsApi.BackgroundJobs.Options;
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
        await TryAutoSelectDoclingAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryAutoSelectDoclingAsync(CancellationToken cancellationToken)
    {
        var state = await _settings.GetServiceEditorStateAsync(
            DocumentIntelligenceOptions.SectionName, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(state.ActiveProviderId)
            && state.ActiveProviderId == ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp)
        {
            return;
        }

        var azureProvider = state.Providers.FirstOrDefault(p =>
            p.ProviderId == ServiceProviderIds.DocumentIntelligenceAzure);
        if (azureProvider is { ConnectionConfigured: true })
        {
            return;
        }

        var doclingBaseUrl = _configuration["LocalServiceHosts:DocumentIntelligenceBaseUrl"]
            ?? "http://localhost:5001";

        if (!await IsReachableAsync(doclingBaseUrl, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Local Docling service at {Url} is not reachable; skipping auto-select",
                doclingBaseUrl);
            return;
        }

        try
        {
            await _settings.EnsureServiceModeExistsAsync(
                DocumentIntelligenceOptions.SectionName,
                ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp,
                cancellationToken).ConfigureAwait(false);

            await _settings.SetServiceActiveProviderAsync(
                DocumentIntelligenceOptions.SectionName,
                ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Auto-selected local Docling as DocumentIntelligence provider (Azure not configured, Docling reachable at {Url})",
                doclingBaseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to auto-select local Docling as DocumentIntelligence provider");
        }
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
