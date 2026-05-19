using System.Text;
using System.Text.Json;
using System.Diagnostics;
using GuideAntsApi.Services.Conversations;

namespace GuideAntsApi.Services
{
    public partial class NotebookImageService
    {
        private async Task<byte[]?> GenerateImageViaLocalSd(string prompt, string size, int n, string outputFormat)
        {
            outputFormat = ResolveLocalOutputFormatFromSettings(outputFormat);
            ValidateLocalSdSize(size);

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(ResolveImageGenerationTimeoutSeconds());

            var endpoint = $"{ResolveLocalSdBaseUrl()}/sd/txt2img";
            var requestBody = new
            {
                prompt,
                size,
                n,
                outputFormat
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            var requestId = AttachLocalSdCorrelationHeaders(request);
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation(
                "Calling local SD txt2img. Endpoint={Endpoint}, RequestId={RequestId}, Size={Size}, N={N}, OutputFormat={OutputFormat}",
                endpoint,
                requestId,
                size,
                n,
                outputFormat);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var reason = response.ReasonPhrase ?? string.Empty;
                var error = ExtractErrorMessage(responseBody);
                _logger.LogWarning(
                    "Local SD txt2img API error. RequestId={RequestId}, StatusCode={StatusCode}, Reason={Reason}, LatencyMs={LatencyMs}, Error={ApiError}",
                    requestId,
                    statusCode,
                    reason,
                    stopwatch.ElapsedMilliseconds,
                    error);
                throw new InvalidOperationException($"Local SD txt2img API error {statusCode} {reason}: {error}");
            }

            _logger.LogInformation(
                "Local SD txt2img succeeded. RequestId={RequestId}, StatusCode={StatusCode}, LatencyMs={LatencyMs}, ResponseBytes={ResponseBytes}",
                requestId,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                responseBody.Length);
            return await SaveResponseAndReturnBytes(responseBody);
        }

        private async Task<byte[]?> GenerateImageEditViaLocalSd(
            string prompt,
            string size,
            int n,
            string outputFormat,
            byte[] imageBytes,
            string? imageContentType,
            string? imageFileName)
        {
            outputFormat = ResolveLocalOutputFormatFromSettings(outputFormat);
            ValidateLocalSdSize(size);

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(ResolveImageGenerationTimeoutSeconds());

            var endpoint = $"{ResolveLocalSdBaseUrl()}/sd/img2img";

            imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(
                imageBytes,
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(size), "size");
            form.Add(new StringContent(prompt ?? string.Empty), "prompt");
            form.Add(new StringContent(n.ToString()), "n");
            form.Add(new StringContent(outputFormat ?? "png"), "outputFormat");

            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            form.Add(imageContent, "image", string.IsNullOrWhiteSpace(imageFileName) ? "image.png" : imageFileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = form
            };
            var requestId = AttachLocalSdCorrelationHeaders(request);
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation(
                "Calling local SD img2img. Endpoint={Endpoint}, RequestId={RequestId}, Size={Size}, N={N}, OutputFormat={OutputFormat}, InputBytes={InputBytes}, InputContentType={InputContentType}, InputFileName={InputFileName}",
                endpoint,
                requestId,
                size,
                n,
                outputFormat,
                imageBytes.Length,
                imageContentType,
                imageFileName);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var reason = response.ReasonPhrase ?? string.Empty;
                var error = ExtractErrorMessage(responseBody);
                _logger.LogWarning(
                    "Local SD img2img API error. RequestId={RequestId}, StatusCode={StatusCode}, Reason={Reason}, LatencyMs={LatencyMs}, Error={ApiError}",
                    requestId,
                    statusCode,
                    reason,
                    stopwatch.ElapsedMilliseconds,
                    error);
                throw new InvalidOperationException($"Local SD img2img API error {statusCode} {reason}: {error}");
            }

            _logger.LogInformation(
                "Local SD img2img succeeded. RequestId={RequestId}, StatusCode={StatusCode}, LatencyMs={LatencyMs}, ResponseBytes={ResponseBytes}",
                requestId,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                responseBody.Length);
            return await SaveResponseAndReturnBytes(responseBody);
        }

        private static string AttachLocalSdCorrelationHeaders(HttpRequestMessage request)
        {
            var activity = Activity.Current;
            var requestId = activity?.TraceId.ToString();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                requestId = Guid.NewGuid().ToString("N");
            }

            request.Headers.TryAddWithoutValidation("x-request-id", requestId);

            if (!string.IsNullOrWhiteSpace(activity?.Id))
            {
                request.Headers.TryAddWithoutValidation("traceparent", activity.Id);
            }

            if (!string.IsNullOrWhiteSpace(activity?.TraceStateString))
            {
                request.Headers.TryAddWithoutValidation("tracestate", activity.TraceStateString);
            }

            return requestId;
        }

        private static void ValidateLocalSdSize(string size)
        {
            if (!CurrentImageSizes.Contains(size))
            {
                throw new ArgumentException(
                    $"Invalid size '{size}'. Valid sizes are: {string.Join(", ", CurrentImageSizes)}",
                    nameof(size));
            }
        }
    }
}
