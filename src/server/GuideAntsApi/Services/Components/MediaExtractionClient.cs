using System.Text.Json;
using GuideAntsApi.BackgroundJobs.Http;
using GuideAntsApi.Options;
using GuideAntsApi.DataModel.Media;
using GuideAntsApi.Services.Core;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Components
{
    public sealed class MediaExtractionClient : IMediaExtractionClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly IOptionsMonitor<LocalServiceHostsOptions> _localServiceHostsOptionsMonitor;
        private readonly IOptionsMonitor<VideoAudioExtractionOptions> _videoAudioExtractionOptionsMonitor;
        private readonly ILogger<MediaExtractionClient> _logger;

        public MediaExtractionClient(
            HttpClient httpClient,
            IOptionsMonitor<LocalServiceHostsOptions> localServiceHostsOptionsMonitor,
            IOptionsMonitor<VideoAudioExtractionOptions> videoAudioExtractionOptionsMonitor,
            ILogger<MediaExtractionClient> logger)
        {
            _httpClient = httpClient;
            _localServiceHostsOptionsMonitor = localServiceHostsOptionsMonitor;
            _videoAudioExtractionOptionsMonitor = videoAudioExtractionOptionsMonitor;
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
            LocalServiceRequestHeaders.ApplyRequestTimeout(
                requestMessage,
                Math.Max(1, _videoAudioExtractionOptionsMonitor.CurrentValue.TimeoutSeconds));

            using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var (error, errorType) = ExtractErrorMessageAndType(responseBody);

                // A valid file with no audio stream is a legitimate empty result, not a
                // failure: surface a typed exception so callers can skip, not retry.
                if (string.Equals(errorType, "NO_AUDIO_STREAM", StringComparison.Ordinal))
                {
                    var sourcePath = ParseSourcePath(error);
                    _logger.LogInformation(
                        "Media extraction reported no audio stream (status {StatusCode}): {Error}",
                        (int)response.StatusCode,
                        error);
                    throw new MediaNoAudioStreamException(sourcePath ?? string.Empty);
                }

                var message = string.IsNullOrWhiteSpace(errorType)
                    ? $"Media extraction API failed ({(int)response.StatusCode}): {error}"
                    : $"Media extraction API failed ({(int)response.StatusCode}): [{errorType}] {error}";
                _logger.LogError(
                    "Media extraction API failed with status code {StatusCode}: {Error}",
                    (int)response.StatusCode,
                    message);
                throw new InvalidOperationException(message);
            }

            var result = JsonSerializer.Deserialize<MediaExtractionResponse>(responseBody, JsonOptions);
            if (result is null)
            {
                throw new InvalidOperationException("Media extraction API returned an empty response body.");
            }

            return result;
        }

        private static string? ParseSourcePath(string error)
        {
            const string marker = "no audio stream: ";
            var index = error.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            return error[(index + marker.Length)..].Trim();
        }

        private static (string Error, string? ErrorType) ExtractErrorMessageAndType(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return ("No error body returned.", null);
            }

            try
            {
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;

                string? errorType = null;
                if (root.TryGetProperty("errorType", out var errorTypeElement)
                    && errorTypeElement.ValueKind == JsonValueKind.String)
                {
                    errorType = errorTypeElement.GetString();
                }

                if (root.TryGetProperty("detail", out var detail))
                {
                    if (detail.ValueKind == JsonValueKind.String)
                    {
                        return (detail.GetString() ?? detail.ToString(), errorType);
                    }

                    if (detail.ValueKind == JsonValueKind.Object)
                    {
                        // Some error payloads nest the fields in the detail object.
                        if (errorType is null
                            && detail.TryGetProperty("errorType", out var nestedErrorType)
                            && nestedErrorType.ValueKind == JsonValueKind.String)
                        {
                            errorType = nestedErrorType.GetString();
                        }

                        string? nestedText = null;
                        if (detail.TryGetProperty("message", out var nestedMessage)
                            && nestedMessage.ValueKind == JsonValueKind.String)
                        {
                            nestedText = nestedMessage.GetString();
                        }

                        if (nestedText is null
                            && detail.TryGetProperty("detail", out var nestedDetail)
                            && nestedDetail.ValueKind == JsonValueKind.String)
                        {
                            nestedText = nestedDetail.GetString();
                        }

                        return (nestedText ?? detail.ToString(), errorType);
                    }

                    return (detail.ToString(), errorType);
                }

                if (root.TryGetProperty("message", out var message))
                {
                    return (message.ValueKind == JsonValueKind.String ? message.GetString() ?? responseBody : message.ToString(), errorType);
                }
            }
            catch
            {
                // Fall back to the raw body when the payload is not JSON.
            }

            return (responseBody.Trim(), null);
        }
    }
}
