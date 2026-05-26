using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services
{
    public partial class NotebookImageService
    {
        private sealed record HfProviderRoute(string Provider, string ProviderId);

        // Tracks modelId:provider combinations that have been rejected at runtime,
        // so failed providers are not retried within the same process lifetime.
        private static readonly ConcurrentDictionary<string, bool> _hfRejectedProviders = new(StringComparer.OrdinalIgnoreCase);

        private static string HfRejectedKey(string modelId, string provider) => $"{modelId}:{provider}";

        private async Task<byte[]?> GenerateImageViaHuggingFace(
            string prompt,
            string size,
            int n,
            string outputFormat,
            string? modelId,
            string? requestPresetJson)
        {
            var token = _configuration["HuggingFace:Token"];
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("HuggingFace:Token is required.");
            }

            var textToImageModelId = ReadServiceModePresetField(requestPresetJson, "TextToImageModelId")
                ?? modelId;
            if (string.IsNullOrWhiteSpace(textToImageModelId))
            {
                throw new RoutingException(
                    RoutingErrorCodes.ProviderNotReady,
                    "HuggingFace image generation requires a TextToImageModelId preset field.",
                    action: "Set TextToImageModelId in the ImageGeneration service-mode preset for HuggingFace.",
                    serviceId: RoutedServiceNames.ImageGeneration,
                    providerSection: HuggingFaceProviderSection);
            }
            var (width, height) = ParseImageSize(size);
            var routes = await ResolveHuggingFaceProvidersAsync(textToImageModelId, "text-to-image", token);
            using var client = _httpClientFactory.CreateClient();

            string? lastError = null;
            foreach (var route in routes)
            {
                if (_hfRejectedProviders.ContainsKey(HfRejectedKey(textToImageModelId, route.Provider)))
                    continue;

                var (endpoint, payload, extraHeaders) = BuildHuggingFaceTextToImageRequest(route, prompt, width, height);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, ProviderPayloadJson), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                foreach (var (key, value) in extraHeaders)
                    request.Headers.TryAddWithoutValidation(key, value);

                using var response = await client.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"{(int)response.StatusCode} {responseBody}";
                    _hfRejectedProviders.TryAdd(HfRejectedKey(textToImageModelId, route.Provider), true);
                    continue;
                }

                return await ReadHuggingFaceImageResultAsync(client, token, route.Provider, responseBody, endpoint);
            }

            throw new InvalidOperationException(
                $"Hugging Face image generation failed: {(lastError ?? "No compatible live provider route was found.")}");
        }

        private async Task<byte[]?> GenerateImageEditViaHuggingFace(
            string prompt,
            string size,
            int n,
            string outputFormat,
            byte[] imageBytes,
            string? imageContentType,
            string? imageFileName,
            string? modelId,
            string? requestPresetJson)
        {
            var token = _configuration["HuggingFace:Token"];
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("HuggingFace:Token is required.");
            }

            var imageToImageModelId = ReadServiceModePresetField(requestPresetJson, "ImageToImageModelId")
                ?? modelId;
            if (string.IsNullOrWhiteSpace(imageToImageModelId))
            {
                throw new RoutingException(
                    RoutingErrorCodes.ProviderNotReady,
                    "HuggingFace image editing requires an ImageToImageModelId preset field.",
                    action: "Set ImageToImageModelId in the ImageGeneration service-mode preset for HuggingFace.",
                    serviceId: RoutedServiceNames.ImageGeneration,
                    providerSection: HuggingFaceProviderSection);
            }

            var resizedImageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(
                imageBytes,
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            var (width, height) = ParseImageSize(size);
            var routes = await ResolveHuggingFaceProvidersAsync(imageToImageModelId, "image-to-image", token);
            using var client = _httpClientFactory.CreateClient();

            var imageDataUrl = $"data:{(string.IsNullOrWhiteSpace(imageContentType) ? "image/png" : imageContentType)};base64,{Convert.ToBase64String(resizedImageBytes)}";

            string? lastError = null;
            foreach (var route in routes)
            {
                if (_hfRejectedProviders.ContainsKey(HfRejectedKey(imageToImageModelId, route.Provider)))
                    continue;

                var (endpoint, payload, extraHeaders) = BuildHuggingFaceImageToImageRequest(route, prompt, imageDataUrl, width, height);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, ProviderPayloadJson), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                foreach (var (key, value) in extraHeaders)
                    request.Headers.TryAddWithoutValidation(key, value);

                using var response = await client.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"{(int)response.StatusCode} {responseBody}";
                    _hfRejectedProviders.TryAdd(HfRejectedKey(imageToImageModelId, route.Provider), true);
                    continue;
                }

                return await ReadHuggingFaceImageResultAsync(client, token, route.Provider, responseBody, endpoint);
            }

            throw new InvalidOperationException(
                $"Hugging Face image edit failed: {(lastError ?? "No compatible live provider route was found.")}");
        }

        private async Task<List<HfProviderRoute>> ResolveHuggingFaceProvidersAsync(string modelId, string task, string token)
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://huggingface.co/api/models/{modelId}?expand[]=inferenceProviderMapping";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Failed to resolve HuggingFace provider for '{modelId}': {(int)resp.StatusCode} {body}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("inferenceProviderMapping", out var mapping)
                || mapping.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Model '{modelId}' has no inference provider mapping.");
            }

            var routes = new List<HfProviderRoute>();
            foreach (var provider in mapping.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object) continue;
                var status = provider.Value.TryGetProperty("status", out var s) ? s.GetString() : null;
                var providerTask = provider.Value.TryGetProperty("task", out var t) ? t.GetString() : null;
                var providerId = provider.Value.TryGetProperty("providerId", out var p) ? p.GetString() : null;

                if (string.Equals(status, "live", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(providerTask, task, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(providerId))
                {
                    routes.Add(new HfProviderRoute(provider.Name, providerId!));
                }
            }

            if (routes.Count == 0)
                throw new InvalidOperationException(
                    $"No live inference provider found for model '{modelId}' and task '{task}'.");

            return routes;
        }

        private (string Endpoint, object Payload, (string Key, string Value)[] ExtraHeaders) BuildHuggingFaceTextToImageRequest(
            HfProviderRoute route, string prompt, int width, int height)
        {
            var provider = route.Provider.ToLowerInvariant();
            return provider switch
            {
                "fal-ai" => (
                    $"{HuggingFaceRouterBaseUrl}/fal-ai/{route.ProviderId}?_subdomain=queue",
                    new Dictionary<string, object> { ["prompt"] = prompt, ["width"] = width, ["height"] = height },
                    Array.Empty<(string, string)>()
                ),
                "replicate" => (
                    route.ProviderId.Contains(':')
                        ? $"{HuggingFaceRouterBaseUrl}/replicate/v1/predictions"
                        : $"{HuggingFaceRouterBaseUrl}/replicate/v1/models/{route.ProviderId}/predictions",
                    new Dictionary<string, object>
                    {
                        ["input"] = new Dictionary<string, object> { ["prompt"] = prompt, ["width"] = width, ["height"] = height },
                        ["version"] = route.ProviderId.Contains(':') ? route.ProviderId.Split(':')[1] : ""
                    },
                    new[] { ("Prefer", "wait") }
                ),
                "wavespeed" => (
                    $"{HuggingFaceRouterBaseUrl}/wavespeed/api/v3/{route.ProviderId}",
                    new Dictionary<string, object> { ["prompt"] = prompt, ["width"] = width, ["height"] = height },
                    Array.Empty<(string, string)>()
                ),
                _ => (
                    $"{HuggingFaceRouterBaseUrl}/{provider}/{route.ProviderId}",
                    new Dictionary<string, object> { ["prompt"] = prompt, ["width"] = width, ["height"] = height },
                    Array.Empty<(string, string)>()
                )
            };
        }

        private (string Endpoint, object Payload, (string Key, string Value)[] ExtraHeaders) BuildHuggingFaceImageToImageRequest(
            HfProviderRoute route, string prompt, string imageDataUrl, int width, int height)
        {
            var provider = route.Provider.ToLowerInvariant();
            return provider switch
            {
                "fal-ai" => (
                    $"{HuggingFaceRouterBaseUrl}/fal-ai/{route.ProviderId}?_subdomain=queue",
                    new Dictionary<string, object> { ["prompt"] = prompt, ["image_urls"] = new[] { imageDataUrl }, ["width"] = width, ["height"] = height },
                    Array.Empty<(string, string)>()
                ),
                "replicate" when route.ProviderId.Contains(':') => (
                    $"{HuggingFaceRouterBaseUrl}/replicate/v1/predictions",
                    new Dictionary<string, object>
                    {
                        ["version"] = route.ProviderId.Split(':')[1],
                        ["input"] = new Dictionary<string, object>
                        {
                            ["prompt"] = prompt, ["image"] = imageDataUrl,
                            ["width"] = width, ["height"] = height
                        }
                    },
                    new[] { ("Prefer", "wait") }
                ),
                "replicate" => (
                    $"{HuggingFaceRouterBaseUrl}/replicate/v1/models/{route.ProviderId}/predictions",
                    new Dictionary<string, object>
                    {
                        ["input"] = new Dictionary<string, object>
                        {
                            ["prompt"] = prompt, ["image"] = imageDataUrl,
                            ["width"] = width, ["height"] = height
                        }
                    },
                    new[] { ("Prefer", "wait") }
                ),
                "wavespeed" => (
                    $"{HuggingFaceRouterBaseUrl}/wavespeed/api/v3/{route.ProviderId}",
                    new Dictionary<string, object> { ["prompt"] = prompt, ["image"] = imageDataUrl, ["width"] = width, ["height"] = height },
                    Array.Empty<(string, string)>()
                ),
                _ => (
                    $"{HuggingFaceRouterBaseUrl}/{provider}/{route.ProviderId}",
                    new Dictionary<string, object> { ["prompt"] = prompt, ["image"] = imageDataUrl, ["width"] = width, ["height"] = height },
                    Array.Empty<(string, string)>()
                )
            };
        }

        private async Task<byte[]?> ReadHuggingFaceImageResultAsync(
            HttpClient client, string token, string provider, string responseBody, string originalEndpoint)
        {
            var prov = provider.ToLowerInvariant();

            if (prov == "replicate")
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("output", out var output))
                {
                    string? imageUrl = output.ValueKind == JsonValueKind.String
                        ? output.GetString()
                        : (output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0)
                            ? output[0].GetString()
                            : null;
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        return await DownloadImageBytesAsync(client, imageUrl);
                    }
                }
                throw new InvalidOperationException($"Replicate returned unexpected response: {responseBody}");
            }

            if (prov == "fal-ai")
            {
                using var queueDoc = JsonDocument.Parse(responseBody);
                if (!queueDoc.RootElement.TryGetProperty("request_id", out var reqIdEl))
                {
                    throw new InvalidOperationException($"fal-ai queue response missing request_id: {responseBody}");
                }
                var requestId = reqIdEl.GetString()!;

                var responseUrl = queueDoc.RootElement.TryGetProperty("response_url", out var respUrlEl)
                    ? respUrlEl.GetString() : null;
                var resultPath = responseUrl != null ? new Uri(responseUrl).AbsolutePath : null;

                var parsedEndpoint = new Uri(originalEndpoint);
                var baseUrl = $"{parsedEndpoint.Scheme}://{parsedEndpoint.Host}/fal-ai";
                var queryParams = parsedEndpoint.Query;
                var pollUrl = $"{baseUrl}{resultPath}{queryParams}";

                for (var attempt = 0; attempt < 240; attempt++)
                {
                    await Task.Delay(500);
                    using var pollReq = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                    pollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var pollResp = await client.SendAsync(pollReq);
                    var pollBody = await pollResp.Content.ReadAsStringAsync();
                    if (!pollResp.IsSuccessStatusCode)
                    {
                        // fal-ai returns 400 {"detail":"Request is still in progress"} while processing
                        if ((int)pollResp.StatusCode == 400
                            && pollBody.Contains("still in progress", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        throw new InvalidOperationException($"fal-ai poll failed: {(int)pollResp.StatusCode} {pollBody}");
                    }

                    using var pollDoc = JsonDocument.Parse(pollBody);
                    if (pollDoc.RootElement.TryGetProperty("images", out var images)
                        && images.ValueKind == JsonValueKind.Array
                        && images.GetArrayLength() > 0
                        && images[0].TryGetProperty("url", out var urlEl))
                    {
                        var imageUrl = urlEl.GetString();
                        if (!string.IsNullOrWhiteSpace(imageUrl))
                        {
                            return await DownloadImageBytesAsync(client, imageUrl);
                        }
                    }

                    if (pollDoc.RootElement.TryGetProperty("status", out var statusEl))
                    {
                        var status = statusEl.GetString();
                        if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"fal-ai task failed: {pollBody}");
                        }
                    }
                }
                throw new InvalidOperationException("fal-ai image generation timed out.");
            }

            if (prov == "wavespeed")
            {
                using var submitDoc = JsonDocument.Parse(responseBody);
                string? statusGetUrl = null;
                if (submitDoc.RootElement.TryGetProperty("data", out var dataEl)
                    && dataEl.TryGetProperty("urls", out var urlsEl)
                    && urlsEl.TryGetProperty("get", out var getEl))
                {
                    statusGetUrl = getEl.GetString();
                }
                if (string.IsNullOrWhiteSpace(statusGetUrl))
                {
                    throw new InvalidOperationException($"wavespeed response missing data.urls.get: {responseBody}");
                }

                var resultPath = new Uri(statusGetUrl).AbsolutePath;
                var pollUrl = $"{HuggingFaceRouterBaseUrl}/wavespeed{resultPath}";

                for (var attempt = 0; attempt < 240; attempt++)
                {
                    await Task.Delay(500);
                    using var pollReq = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                    pollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var pollResp = await client.SendAsync(pollReq);
                    var pollBody = await pollResp.Content.ReadAsStringAsync();
                    if (!pollResp.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"wavespeed poll failed: {(int)pollResp.StatusCode} {pollBody}");
                    }

                    using var pollDoc = JsonDocument.Parse(pollBody);
                    if (pollDoc.RootElement.TryGetProperty("data", out var pollData)
                        && pollData.TryGetProperty("status", out var pollStatus))
                    {
                        var st = pollStatus.GetString();
                        if (string.Equals(st, "completed", StringComparison.OrdinalIgnoreCase))
                        {
                            if (pollData.TryGetProperty("outputs", out var outputs)
                                && outputs.ValueKind == JsonValueKind.Array
                                && outputs.GetArrayLength() > 0)
                            {
                                var imageUrl = outputs[0].GetString();
                                if (!string.IsNullOrWhiteSpace(imageUrl))
                                {
                                    return await DownloadImageBytesAsync(client, imageUrl);
                                }
                            }
                            throw new InvalidOperationException($"wavespeed completed but no outputs: {pollBody}");
                        }
                        if (string.Equals(st, "failed", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"wavespeed task failed: {pollBody}");
                        }
                    }
                }
                throw new InvalidOperationException("wavespeed image generation timed out.");
            }

            if (responseBody.Length > 0 && responseBody[0] != '{' && responseBody[0] != '[')
            {
                return Encoding.UTF8.GetBytes(responseBody);
            }
            throw new InvalidOperationException($"Unsupported HuggingFace provider '{provider}' response: {responseBody[..Math.Min(200, responseBody.Length)]}");
        }

        private async Task<byte[]> DownloadImageBytesAsync(HttpClient client, string imageUrl)
        {
            using var imageResp = await client.GetAsync(imageUrl);
            if (!imageResp.IsSuccessStatusCode)
            {
                var err = await imageResp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Failed to download image: {(int)imageResp.StatusCode} {err}");
            }
            return await imageResp.Content.ReadAsByteArrayAsync();
        }
    }
}
