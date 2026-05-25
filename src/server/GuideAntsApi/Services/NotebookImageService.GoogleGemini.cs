using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services
{
    public partial class NotebookImageService
    {
        private async Task<byte[]?> GenerateImageViaGoogleGemini(string prompt, string size, int n, string outputFormat, string? modelId)
        {
            _ = outputFormat;
            var apiKey = _configuration["GoogleGeminiApi:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("GoogleGeminiApi:ApiKey is required.");
            }

            if (string.IsNullOrWhiteSpace(modelId))
            {
                throw new RoutingException(
                    RoutingErrorCodes.ProviderNotReady,
                    "ImageGeneration mode for GoogleGeminiApi must include a model id.",
                    action: "Set ServiceModes.ImageGeneration model id for GoogleGeminiApi.",
                    serviceId: RoutedServiceNames.ImageGeneration,
                    providerSection: GoogleGeminiProviderSection);
            }
            var model = modelId;
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/{NormalizeGoogleGeminiModelName(model)}:generateContent";

            var requestBody = new GoogleGeminiGenerateContentRequest(
                Contents:
                [
                    new GoogleGeminiContent(
                        "user",
                        [
                            new GoogleGeminiPart(Text: prompt)
                        ])
                ],
                GenerationConfig: new GoogleGeminiImageGenerationConfig(
                    ResponseModalities: ["IMAGE"],
                    CandidateCount: Math.Max(1, n),
                    ImageConfig: new GoogleGeminiImageConfig(
                        GetAspectRatioForImageSize(size),
                        GetGoogleGeminiImageSize(size))));

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", apiKey);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Google Gemini image generation failed: {(int)response.StatusCode} {responseBody}");
            }

            return await SaveResponseAndReturnBytes(responseBody);
        }

        private async Task<byte[]?> GenerateImageEditViaGoogleGemini(
            string prompt,
            string size,
            int n,
            string outputFormat,
            byte[] imageBytes,
            string? imageContentType,
            string? imageFileName,
            string? modelId)
        {
            _ = outputFormat;
            var model = RequireImageModelId(
                GoogleGeminiProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for GoogleGeminiApi.");
            var apiKey = _configuration["GoogleGeminiApi:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("GoogleGeminiApi:ApiKey is required.");
            }

            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/{NormalizeGoogleGeminiModelName(model)}:generateContent";

            imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(
                imageBytes,
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            var requestBody = new GoogleGeminiGenerateContentRequest(
                Contents:
                [
                    new GoogleGeminiContent(
                        "user",
                        [
                            new GoogleGeminiPart(Text: prompt),
                            new GoogleGeminiPart(
                                InlineData: new GoogleGeminiBlob(
                                    string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType,
                                    Convert.ToBase64String(imageBytes)))
                        ])
                ],
                GenerationConfig: new GoogleGeminiImageGenerationConfig(
                    ResponseModalities: ["IMAGE"],
                    CandidateCount: Math.Max(1, n),
                    ImageConfig: new GoogleGeminiImageConfig(
                        GetAspectRatioForImageSize(size),
                        GetGoogleGeminiImageSize(size))));

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", apiKey);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Google Gemini image edit failed: {(int)response.StatusCode} {responseBody}");
            }

            return await SaveResponseAndReturnBytes(responseBody);
        }

        private static string GetAspectRatioForImageSize(string size) => size switch
        {
            "1024x1792" => "9:16",
            "1792x1024" => "16:9",
            _ => "1:1"
        };

        private static string GetGoogleGeminiImageSize(string size)
        {
            var parts = size.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var width)
                || !int.TryParse(parts[1], out var height))
            {
                return "1K";
            }

            var maxEdge = Math.Max(width, height);
            return maxEdge switch
            {
                <= 512 => "512",
                <= 1024 => "1K",
                <= 2048 => "2K",
                _ => "4K"
            };
        }

        private static string NormalizeGoogleGeminiModelName(string modelId)
        {
            var trimmed = modelId.Trim();
            return trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"models/{trimmed}";
        }

        private sealed record GoogleGeminiGenerateContentRequest(
            IReadOnlyList<GoogleGeminiContent> Contents,
            GoogleGeminiImageGenerationConfig GenerationConfig);

        private sealed record GoogleGeminiImageGenerationConfig(
            IReadOnlyList<string> ResponseModalities,
            int CandidateCount,
            GoogleGeminiImageConfig ImageConfig);

        private sealed record GoogleGeminiImageConfig(
            string AspectRatio,
            string ImageSize);

        private sealed record GoogleGeminiContent(
            string Role,
            IReadOnlyList<GoogleGeminiPart> Parts);

        private sealed record GoogleGeminiPart(
            string? Text = null,
            GoogleGeminiBlob? InlineData = null);

        private sealed record GoogleGeminiBlob(string MimeType, string Data);
    }
}
