using System.Text.Json;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services
{
    public partial class NotebookImageService
    {
        private static string RequireImageModelId(string providerSection, string? modelId, string action)
        {
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                return modelId;
            }

            throw new RoutingException(
                RoutingErrorCodes.ProviderNotReady,
                $"ImageGeneration mode for {providerSection} must include a model id.",
                action: action,
                serviceId: RoutedServiceNames.ImageGeneration,
                providerSection: providerSection);
        }

        private static string? ReadServiceModePresetField(string? requestPresetJson, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(requestPresetJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(requestPresetJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty(fieldName, out var node))
                {
                    return null;
                }

                return node.ValueKind == JsonValueKind.String
                    ? node.GetString()?.Trim()
                    : node.ToString().Trim();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int? ReadServiceModePresetInt(string? requestPresetJson, string fieldName)
        {
            var raw = ReadServiceModePresetField(requestPresetJson, fieldName);
            return int.TryParse(raw, out var value) && value > 0 ? value : null;
        }

        private static long? ReadServiceModePresetLong(string? requestPresetJson, string fieldName)
        {
            var raw = ReadServiceModePresetField(requestPresetJson, fieldName);
            return long.TryParse(raw, out var value) ? value : null;
        }

        private static double ReadServiceModePresetDouble(string? requestPresetJson, string fieldName, double defaultValue)
        {
            var raw = ReadServiceModePresetField(requestPresetJson, fieldName);
            return double.TryParse(raw, out var value) ? value : defaultValue;
        }

        private static (int width, int height) ParseImageSize(string size)
        {
            var parts = size.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var width)
                || !int.TryParse(parts[1], out var height)
                || width <= 0
                || height <= 0)
            {
                return (1024, 1024);
            }

            return (width, height);
        }

        private static string BuildDataUrl(string contentType, byte[] bytes) =>
            $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";

        private static string[] GetValidImageSizes(string deploymentName)
        {
            if (string.Equals(deploymentName, "google-imagen", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(deploymentName, "hf-image", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(deploymentName, "openrouter-image", StringComparison.OrdinalIgnoreCase))
            {
                return CurrentImageSizes;
            }

            if (deploymentName.Contains("flux", StringComparison.OrdinalIgnoreCase))
            {
                return CurrentImageSizes;
            }

            if (string.Equals(deploymentName, "gpt-image-1.5", StringComparison.OrdinalIgnoreCase))
            {
                return GptImage15Sizes;
            }

            return CurrentImageSizes;
        }

        /// <summary>
        /// Determines the best output size based on the source image dimensions.
        /// Returns the closest matching size from: 1024x1024 (square), 1024x1792 (portrait), 1792x1024 (landscape)
        /// </summary>
        private string DetermineBestSizeForImage(byte[] imageBytes, string deploymentName)
        {
            try
            {
                var (width, height) = GetImageDimensions(imageBytes);

                if (width <= 0 || height <= 0)
                {
                    _logger.LogWarning("Unable to determine image dimensions, defaulting to 1024x1024");
                    return "1024x1024";
                }

                var useCurrentSizes = deploymentName.Contains("flux", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(deploymentName, "gpt-image-1.5", StringComparison.OrdinalIgnoreCase);

                double aspectRatio = (double)width / height;
                _logger.LogInformation("Source image dimensions: {Width}x{Height}, aspect ratio: {AspectRatio:F2}", width, height, aspectRatio);

                const double squareRatio = 1.0;
                double portraitRatio = useCurrentSizes ? (1024.0 / 1792.0) : (1024.0 / 1536.0);
                double landscapeRatio = useCurrentSizes ? (1792.0 / 1024.0) : (1536.0 / 1024.0);

                double squareDiff = Math.Abs(aspectRatio - squareRatio);
                double portraitDiff = Math.Abs(aspectRatio - portraitRatio);
                double landscapeDiff = Math.Abs(aspectRatio - landscapeRatio);

                if (portraitDiff < squareDiff && portraitDiff < landscapeDiff)
                {
                    var portraitSize = useCurrentSizes ? "1024x1792" : "1024x1536";
                    _logger.LogInformation("Selected portrait orientation ({Size})", portraitSize);
                    return portraitSize;
                }
                else if (landscapeDiff < squareDiff && landscapeDiff < portraitDiff)
                {
                    var landscapeSize = useCurrentSizes ? "1792x1024" : "1536x1024";
                    _logger.LogInformation("Selected landscape orientation ({Size})", landscapeSize);
                    return landscapeSize;
                }
                else
                {
                    _logger.LogInformation("Selected square orientation (1024x1024)");
                    return "1024x1024";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to determine image dimensions, defaulting to 1024x1024");
                return "1024x1024";
            }
        }

        /// <summary>
        /// Extracts image dimensions from common image format headers (PNG, JPEG, GIF, BMP, WEBP)
        /// </summary>
        private static (int width, int height) GetImageDimensions(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length < 24)
                return (0, 0);

            // PNG: Starts with 89 50 4E 47, dimensions at offset 16-23
            if (imageBytes[0] == 0x89 && imageBytes[1] == 0x50 && imageBytes[2] == 0x4E && imageBytes[3] == 0x47)
            {
                int width = (imageBytes[16] << 24) | (imageBytes[17] << 16) | (imageBytes[18] << 8) | imageBytes[19];
                int height = (imageBytes[20] << 24) | (imageBytes[21] << 16) | (imageBytes[22] << 8) | imageBytes[23];
                return (width, height);
            }

            // JPEG: Starts with FF D8
            if (imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
            {
                int pos = 2;
                while (pos < imageBytes.Length - 9)
                {
                    if (imageBytes[pos] != 0xFF)
                        break;

                    byte marker = imageBytes[pos + 1];
                    if (marker >= 0xC0 && marker <= 0xC3)
                    {
                        int height = (imageBytes[pos + 5] << 8) | imageBytes[pos + 6];
                        int width = (imageBytes[pos + 7] << 8) | imageBytes[pos + 8];
                        return (width, height);
                    }

                    int segmentLength = (imageBytes[pos + 2] << 8) | imageBytes[pos + 3];
                    pos += 2 + segmentLength;
                }
                return (0, 0);
            }

            // GIF: Starts with 47 49 46 (GIF), dimensions at offset 6-9
            if (imageBytes[0] == 0x47 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46)
            {
                int width = imageBytes[6] | (imageBytes[7] << 8);
                int height = imageBytes[8] | (imageBytes[9] << 8);
                return (width, height);
            }

            // BMP: Starts with 42 4D (BM), dimensions at offset 18-25
            if (imageBytes[0] == 0x42 && imageBytes[1] == 0x4D && imageBytes.Length >= 26)
            {
                int width = imageBytes[18] | (imageBytes[19] << 8) | (imageBytes[20] << 16) | (imageBytes[21] << 24);
                int height = imageBytes[22] | (imageBytes[23] << 8) | (imageBytes[24] << 16) | (imageBytes[25] << 24);
                return (width, Math.Abs(height));
            }

            // WEBP: Starts with RIFF and contains WEBP
            if (imageBytes.Length >= 30 &&
                imageBytes[0] == 0x52 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46 && imageBytes[3] == 0x46 &&
                imageBytes[8] == 0x57 && imageBytes[9] == 0x45 && imageBytes[10] == 0x42 && imageBytes[11] == 0x50)
            {
                if (imageBytes[12] == 0x56 && imageBytes[13] == 0x50 && imageBytes[14] == 0x38 && imageBytes[15] == 0x4C)
                {
                    int width = (((imageBytes[21] & 0x3F) << 8) | imageBytes[20]) + 1;
                    int height = (((imageBytes[23] & 0xF) << 10) | (imageBytes[22] << 2) | ((imageBytes[21] & 0xC0) >> 6)) + 1;
                    return (width, height);
                }
                if (imageBytes.Length >= 32 && imageBytes[12] == 0x56 && imageBytes[13] == 0x50 && imageBytes[14] == 0x38 && imageBytes[15] == 0x20)
                {
                    int width = ((imageBytes[26] << 8) | imageBytes[27]) & 0x3FFF;
                    int height = ((imageBytes[28] << 8) | imageBytes[29]) & 0x3FFF;
                    return (width, height);
                }
            }

            return (0, 0);
        }

        /// <summary>
        /// Saves response and returns the first image bytes from a JSON response.
        /// Handles OpenAI, Google Gemini, OpenRouter, and Azure OpenAI response shapes.
        /// </summary>
        private Task<byte[]?> SaveResponseAndReturnBytes(string responseJson)
        {
            try
            {
                var json = JsonDocument.Parse(responseJson);

                if (json.RootElement.TryGetProperty("error", out var errorElement))
                {
                    var code = errorElement.TryGetProperty("code", out var codeEl) ? codeEl.ToString() : null;
                    var message = errorElement.TryGetProperty("message", out var msgEl) ? msgEl.ToString() : null;
                    var status = errorElement.TryGetProperty("status", out var statusEl) ? statusEl.ToString() : null;
                    var composed = string.Join(" ", new[] { status, code }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    var finalMessage = string.IsNullOrWhiteSpace(composed) ? (message ?? "Unknown error") : ($"{composed}: {message}");
                    throw new InvalidOperationException(finalMessage);
                }

                if (json.RootElement.TryGetProperty("data", out var data))
                {
                    if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                    {
                        var firstImage = data[0];
                        if (firstImage.TryGetProperty("b64_json", out var b64Property))
                        {
                            var b64 = b64Property.GetString();
                            if (!string.IsNullOrEmpty(b64))
                            {
                                var bytes = Convert.FromBase64String(b64);
                                _logger.LogInformation("Image generated successfully, size: {Size} bytes", bytes.Length);
                                return Task.FromResult<byte[]?>(bytes);
                            }
                        }
                    }
                }

                if (TryExtractOpenRouterChatImageBytes(json.RootElement, out var openRouterBytes))
                {
                    return Task.FromResult<byte[]?>(openRouterBytes);
                }

                if (TryExtractGoogleGeminiImageBytes(json.RootElement, out var googleGeminiBytes))
                {
                    return Task.FromResult<byte[]?>(googleGeminiBytes);
                }

                if (json.RootElement.TryGetProperty("predictions", out var predictions) &&
                    predictions.ValueKind == JsonValueKind.Array &&
                    predictions.GetArrayLength() > 0)
                {
                    var first = predictions[0];
                    if (first.TryGetProperty("bytesBase64Encoded", out var bytesEl))
                    {
                        var b64 = bytesEl.GetString();
                        if (!string.IsNullOrWhiteSpace(b64))
                        {
                            return Task.FromResult<byte[]?>(Convert.FromBase64String(b64));
                        }
                    }
                }

                _logger.LogError("Image generation response did not contain data. Raw response: {Response}", responseJson);
                return Task.FromResult<byte[]?>(null);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to parse image generation response: {Message}. Raw response: {Response}", LogValueSanitizer.Sanitize(ex.Message), LogValueSanitizer.Sanitize(responseJson));
                return Task.FromResult<byte[]?>(null);
            }
        }

        private static string ExtractErrorMessage(string responseJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var codeEl) ? codeEl.ToString() : null;
                    var message = error.TryGetProperty("message", out var msgEl) ? msgEl.ToString() : null;
                    var status = error.TryGetProperty("status", out var statusEl) ? statusEl.ToString() : null;
                    var composed = string.Join(" ", new[] { status, code }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    return string.IsNullOrWhiteSpace(composed) ? (message ?? "") : ($"{composed}: {message}");
                }
                if (root.TryGetProperty("message", out var msg))
                {
                    return msg.ToString();
                }
            }
            catch
            {
                // fall back to raw json
            }
            return responseJson;
        }

        private static bool TryExtractGoogleGeminiImageBytes(JsonElement root, out byte[]? bytes)
        {
            bytes = null;
            if (!root.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                return false;
            }

            foreach (var candidate in candidates.EnumerateArray())
            {
                if (!candidate.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Object
                    || !content.TryGetProperty("parts", out var parts)
                    || parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("inlineData", out var inlineData)
                        || inlineData.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!inlineData.TryGetProperty("data", out var dataElement))
                    {
                        continue;
                    }

                    var base64 = dataElement.GetString();
                    if (!string.IsNullOrWhiteSpace(base64))
                    {
                        bytes = Convert.FromBase64String(base64);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryExtractOpenRouterChatImageBytes(JsonElement root, out byte[]? bytes)
        {
            bytes = null;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return false;
            }

            var message = choices[0].TryGetProperty("message", out var messageElement)
                ? messageElement
                : default;

            if (message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("images", out var images)
                    && TryExtractImageBytesFromImageCollection(images, out bytes))
                {
                    return true;
                }

                if (message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (part.TryGetProperty("image_url", out var imageUrl)
                            && TryExtractImageBytesFromImageUrl(imageUrl, out bytes))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryExtractImageBytesFromImageCollection(JsonElement images, out byte[]? bytes)
        {
            bytes = null;
            if (images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            {
                return false;
            }

            var first = images[0];
            if (first.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (first.TryGetProperty("image_url", out var imageUrl))
            {
                return TryExtractImageBytesFromImageUrl(imageUrl, out bytes);
            }

            if (first.TryGetProperty("b64_json", out var b64Property))
            {
                var base64 = b64Property.GetString();
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    bytes = Convert.FromBase64String(base64);
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractImageBytesFromImageUrl(JsonElement imageUrl, out byte[]? bytes)
        {
            bytes = null;
            var url = imageUrl.ValueKind == JsonValueKind.Object && imageUrl.TryGetProperty("url", out var urlProp)
                ? urlProp.GetString()
                : imageUrl.GetString();
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var commaIndex = url.IndexOf(',');
            if (commaIndex < 0)
            {
                return false;
            }

            bytes = Convert.FromBase64String(url[(commaIndex + 1)..]);
            return true;
        }
    }
}
