using System.Text;
using System.Text.Json;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services
{
    public partial class NotebookImageService
    {
        private async Task<byte[]?> GenerateImageViaAzureOpenAI(string prompt, string size, int n, string outputFormat, ServiceMode mode)
        {
            try
            {
                var endpoint = _configuration["AzureOpenAiImages:Endpoint"] ?? throw new Exception("Bad config");
                var deployment = !string.IsNullOrWhiteSpace(mode.ModelId)
                    ? mode.ModelId.Trim()
                    : throw new InvalidOperationException("ImageGeneration Azure service mode must include Deployment/ModelId.");
                var apiVersion = _configuration["AzureOpenAiImages:ApiVersion"] ?? throw new Exception("Bad config:ApiVersion");
                var apiKey = _configuration["AzureOpenAiImages:ApiKey"];

                var validSizes = GetValidImageSizes(deployment);
                if (!validSizes.Contains(size))
                {
                    throw new ArgumentException($"Invalid size '{size}'. Valid sizes are: {string.Join(", ", validSizes)}", nameof(size));
                }

                var basePath = $"openai/deployments/{deployment}/images";
                var urlParams = $"?api-version={apiVersion}";

                using var client = _httpClientFactory.CreateClient();

                var generationUrl = $"{endpoint}{basePath}/generations{urlParams}";

                object generationBody = new
                {
                    prompt = prompt,
                    n = n,
                    size = size,
                    model = deployment,
                };

                using (var genRequest = new HttpRequestMessage(HttpMethod.Post, generationUrl))
                {
                    genRequest.Headers.Add("Api-Key", apiKey);
                    var json = JsonSerializer.Serialize(generationBody);
                    genRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    var genResponse = await client.SendAsync(genRequest);
                    var genResult = await genResponse.Content.ReadAsStringAsync();

                    if (!genResponse.IsSuccessStatusCode)
                    {
                        var statusCode = (int)genResponse.StatusCode;
                        var reason = genResponse.ReasonPhrase ?? "";
                        var apiError = ExtractErrorMessage(genResult);
                        _logger.LogWarning("Image API error {StatusCode} {Reason}: {ApiError}", statusCode, reason, apiError);
                        throw new InvalidOperationException($"Image API error {statusCode} {reason}: {apiError}");
                    }

                    return await SaveResponseAndReturnBytes(genResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate image via Azure OpenAI");
                throw;
            }
        }

        /// <summary>
        /// Generates an image edit via Azure OpenAI Images Edits endpoint using multipart/form-data.
        /// Requires the Azure image service mode preset to include EditModelDeployment.
        /// </summary>
        private async Task<byte[]?> GenerateImageEditViaAzureOpenAI(
            string prompt,
            string size,
            int n,
            byte[] imageBytes,
            string imageContentType,
            string imageFileName,
            ServiceMode mode)
        {
            var endpoint = _configuration["AzureOpenAiImages:Endpoint"] ?? throw new InvalidOperationException("AzureOpenAiImages:Endpoint is not configured");
            var editDeployment = ReadServiceModePresetField(mode.RequestPresetJson, "EditModelDeployment")
                ?? throw new InvalidOperationException(
                    "ImageGeneration Azure service mode preset must include EditModelDeployment.");
            var apiVersion = _configuration["AzureOpenAiImages:ApiVersion"] ?? "2025-04-01-preview";
            var apiKey = _configuration["AzureOpenAiImages:ApiKey"] ?? throw new InvalidOperationException("AzureOpenAiImages:ApiKey is not configured");

            var validSizes = GetValidImageSizes(editDeployment);
            if (!validSizes.Contains(size))
            {
                throw new ArgumentException($"Invalid size '{size}'. Valid sizes are: {string.Join(", ", validSizes)}", nameof(size));
            }

            var basePath = $"openai/deployments/{editDeployment}/images";
            var url = $"{endpoint}{basePath}/edits?api-version={apiVersion}";

            using var client = _httpClientFactory.CreateClient();

            imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(imageBytes, imageContentType);
            _logger.LogInformation("Image prepared for edit API. Size: {Size} bytes", imageBytes.Length);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(size), "size");
            form.Add(new StringContent(prompt ?? string.Empty), "prompt");
            form.Add(new StringContent(n.ToString()), "n");
            form.Add(new StringContent(editDeployment.ToLowerInvariant()), "model");

            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            form.Add(imageContent, "image", string.IsNullOrWhiteSpace(imageFileName) ? "image.png" : imageFileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = form
            };

            request.Headers.Add("Api-Key", apiKey);

            using var response = await client.SendAsync(request);
            var result = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var reason = response.ReasonPhrase ?? string.Empty;
                var apiError = ExtractErrorMessage(result);
                _logger.LogWarning("Image edits API error {StatusCode} {Reason}: {ApiError}", statusCode, reason, apiError);
                throw new InvalidOperationException($"Image edits API error {statusCode} {reason}: {apiError}");
            }

            return await SaveResponseAndReturnBytes(result);
        }
    }
}
