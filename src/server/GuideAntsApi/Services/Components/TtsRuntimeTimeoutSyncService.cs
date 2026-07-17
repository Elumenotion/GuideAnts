using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GuideAntsApi.Endpoints;

namespace GuideAntsApi.Services.Components;

public interface ITtsRuntimeTimeoutSyncService
{
    Task SyncReadyTimeoutAsync(int readyTimeoutSeconds, CancellationToken cancellationToken = default);
}

public sealed class TtsRuntimeTimeoutSyncService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<TtsRuntimeTimeoutSyncService> logger) : ITtsRuntimeTimeoutSyncService
{
    public async Task SyncReadyTimeoutAsync(int readyTimeoutSeconds, CancellationToken cancellationToken = default)
    {
        if (readyTimeoutSeconds <= 0)
        {
            return;
        }

        var adminBase = LocalServiceAdminRouting.ResolveAdminBase("SpeechSynthesis", configuration);
        if (adminBase is null)
        {
            logger.LogDebug("Skipping TTS ready timeout sync because SpeechSynthesis admin base URL is not configured.");
            return;
        }

        var endpoint = $"{adminBase.TrimEnd('/')}/admin/runtime-timeouts";
        using var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
        {
            Content = JsonContent.Create(new RuntimeTimeoutsPayload(readyTimeoutSeconds))
        };

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                logger.LogWarning(
                    "TTS ready timeout sync to {Endpoint} failed with status {StatusCode}: {Body}",
                    endpoint,
                    (int)response.StatusCode,
                    body);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "TTS ready timeout sync to {Endpoint} failed.", endpoint);
        }
    }

    private sealed record RuntimeTimeoutsPayload(
        [property: JsonPropertyName("readyTimeoutSeconds")] int ReadyTimeoutSeconds);
}
