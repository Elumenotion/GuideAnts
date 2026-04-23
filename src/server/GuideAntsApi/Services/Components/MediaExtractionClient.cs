using System.Text.Json;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Core;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Components
{
    public sealed class MediaExtractionClient : IMediaExtractionClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly IOptionsMonitor<LocalServiceHostsOptions> _localServiceHostsOptionsMonitor;
        private readonly ILogger<MediaExtractionClient> _logger;

        public MediaExtractionClient(
            HttpClient httpClient,
            IOptionsMonitor<LocalServiceHostsOptions> localServiceHostsOptionsMonitor,
            ILogger<MediaExtractionClient> logger)
        {
            _httpClient = httpClient;
            _localServiceHostsOptionsMonitor = localServiceHostsOptionsMonitor;
            _logger = logger;
        }

        public async Task<MediaExtractionResponse> ExtractAudioAsync(
            MediaExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            var localHosts = _localServiceHostsOptionsMonitor.CurrentValue;
            if (string.IsNullOrWhiteSpace(localHosts.MediaBaseUrl))
            {
                throw new InvalidOperationException(
                    "LocalServiceHosts:MediaBaseUrl is required for local media extraction.");
            }

            var endpoint = $"{localHosts.MediaBaseUrl.TrimEnd('/')}/media/extract-audio";
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };

            using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = ExtractErrorMessage(responseBody);
                _logger.LogError(
                    "Media extraction API failed with status code {StatusCode}: {Error}",
                    (int)response.StatusCode,
                    error);
                throw new InvalidOperationException(
                    $"Media extraction API failed ({(int)response.StatusCode}): {error}");
            }

            var result = JsonSerializer.Deserialize<MediaExtractionResponse>(responseBody, JsonOptions);
            if (result is null)
            {
                throw new InvalidOperationException("Media extraction API returned an empty response body.");
            }

            return result;
        }

        private static string ExtractErrorMessage(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return "No error body returned.";
            }

            try
            {
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;

                if (root.TryGetProperty("detail", out var detail))
                {
                    return detail.ValueKind == JsonValueKind.String ? detail.GetString() ?? responseBody : detail.ToString();
                }

                if (root.TryGetProperty("message", out var message))
                {
                    return message.ValueKind == JsonValueKind.String ? message.GetString() ?? responseBody : message.ToString();
                }
            }
            catch
            {
                // Fall back to the raw body when the payload is not JSON.
            }

            return responseBody.Trim();
        }
    }
}
