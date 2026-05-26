using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services
{
    public partial class NotebookImageService
    {
        private async Task<byte[]?> GenerateImageViaOpenRouter(
            string prompt,
            string size,
            int n,
            string outputFormat,
            string? modelId,
            string? requestPresetJson)
        {
            _ = outputFormat;
            var apiKey = _configuration["OpenRouter:ApiKey"];
            var baseUrl = _configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenRouter:ApiKey is required.");
            }

            if (string.IsNullOrWhiteSpace(modelId))
            {
                throw new RoutingException(
                    RoutingErrorCodes.ProviderNotReady,
                    "ImageGeneration mode for OpenRouter must include a model id.",
                    action: "Set ServiceModes.ImageGeneration model id for OpenRouter.",
                    serviceId: RoutedServiceNames.ImageGeneration,
                    providerSection: OpenRouterProviderSection);
            }
            var model = modelId;
            ValidateOpenRouterImageModel(model, requestPresetJson);
            var endpoint = $"{baseUrl.TrimEnd('/')}/chat/completions";
            var requestBody = new OpenRouterImageChatRequest(
                Model: model,
                Messages:
                [
                    new OpenRouterImageChatMessage(
                        "user",
                        [
                            new OpenRouterImageContentPart("text", prompt, null)
                        ])
                ],
                Modalities: ["image"],
                N: Math.Max(1, n),
                Size: size);

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenRouter image generation failed: {(int)response.StatusCode} {responseBody}");
            }

            return await SaveResponseAndReturnBytes(responseBody);
        }

        private async Task<byte[]?> GenerateImageEditViaOpenRouter(
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
            _ = outputFormat;
            var model = RequireImageModelId(
                OpenRouterProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for OpenRouter.");
            ValidateOpenRouterImageModel(model, requestPresetJson);
            var apiKey = _configuration["OpenRouter:ApiKey"];
            var baseUrl = _configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenRouter:ApiKey is required.");
            }

            imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(
                imageBytes,
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            var endpoint = $"{baseUrl.TrimEnd('/')}/chat/completions";
            var dataUrl = BuildDataUrl(
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType,
                imageBytes);
            var requestBody = new OpenRouterImageChatRequest(
                Model: model,
                Messages:
                [
                    new OpenRouterImageChatMessage(
                        "user",
                        [
                            new OpenRouterImageContentPart("text", prompt ?? string.Empty, null),
                            new OpenRouterImageContentPart(
                                "image_url",
                                null,
                                new OpenRouterImageUrl(dataUrl))
                        ])
                ],
                Modalities: ["image"],
                N: Math.Max(1, n),
                Size: size);

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request);
            var result = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenRouter image edit failed: {(int)response.StatusCode} {result}");
            }

            return await SaveResponseAndReturnBytes(result);
        }

        private sealed record OpenRouterImageChatRequest(
            string Model,
            IReadOnlyList<OpenRouterImageChatMessage> Messages,
            IReadOnlyList<string> Modalities,
            int N,
            string Size);

        private sealed record OpenRouterImageChatMessage(
            string Role,
            IReadOnlyList<OpenRouterImageContentPart> Content);

        private sealed record OpenRouterImageContentPart(
            string Type,
            string? Text,
            [property: JsonPropertyName("image_url")] OpenRouterImageUrl? ImageUrl);

        private sealed record OpenRouterImageUrl(string Url);
    }
}
