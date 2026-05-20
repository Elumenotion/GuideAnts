using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace GuideAntsApi.Services
{
    public partial class NotebookImageService
    {
        private async Task<byte[]?> GenerateImageViaOpenAi(
            string prompt,
            string size,
            int n,
            string? modelId)
        {
            var apiKey = _configuration["OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenAI:ApiKey is required.");
            }

            var model = RequireImageModelId(
                OpenAiProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for OpenAI.");

            var baseUrl = (_configuration["OpenAI:Endpoint"] ?? "https://api.openai.com/v1").TrimEnd('/');
            var endpoint = $"{baseUrl}/images/generations";
            var requestBody = new Dictionary<string, object>
            {
                ["model"] = model,
                ["prompt"] = prompt,
                ["n"] = Math.Max(1, n),
                ["size"] = size,
            };
            if (OpenAiImageModelUsesLegacyResponseFormat(model))
            {
                requestBody["response_format"] = "b64_json";
            }

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAI image generation failed: {(int)response.StatusCode} {responseBody}");
            }

            return await SaveResponseAndReturnBytes(responseBody);
        }

        private async Task<byte[]?> GenerateImageEditViaOpenAi(
            string prompt,
            string size,
            int n,
            byte[] imageBytes,
            string? imageContentType,
            string? imageFileName,
            string? modelId)
        {
            var apiKey = _configuration["OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenAI:ApiKey is required.");
            }

            var model = RequireImageModelId(
                OpenAiProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for OpenAI.");

            var baseUrl = (_configuration["OpenAI:Endpoint"] ?? "https://api.openai.com/v1").TrimEnd('/');
            var endpoint = $"{baseUrl}/images/edits";

            using var content = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            content.Add(imageContent, "image", imageFileName ?? "image.png");
            content.Add(new StringContent(prompt), "prompt");
            content.Add(new StringContent(model), "model");
            content.Add(new StringContent(size), "size");
            content.Add(new StringContent(Math.Max(1, n).ToString()), "n");
            if (OpenAiImageModelUsesLegacyResponseFormat(model))
            {
                content.Add(new StringContent("b64_json"), "response_format");
            }

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request);
            var result = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAI image edit failed: {(int)response.StatusCode} {result}");
            }

            return await SaveResponseAndReturnBytes(result);
        }

        /// <summary>
        /// DALL·E 2/3 accept <c>response_format</c> (<c>url</c> vs <c>b64_json</c>). GPT Image models
        /// return <c>data[].b64_json</c> by default and reject this parameter.
        /// </summary>
        private static bool OpenAiImageModelUsesLegacyResponseFormat(string modelId)
        {
            var m = modelId.Trim();
            if (m.Length == 0)
            {
                return true;
            }

            return !m.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);
        }
    }
}
