using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal sealed class OpenRouterEmbeddingService(
    HttpClient client,
    IConfiguration configuration) : IEmbeddingService
{
    private readonly HttpClient _client = client;
    private readonly IConfiguration _configuration = configuration;

    public Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "OpenRouter embeddings require an explicit model id from ServiceModes. " +
            "Use ProviderRoutedEmbeddingService with mode.ModelId.");
    }

    public async Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        string modelId,
        string? requestPresetJson,
        CancellationToken cancellationToken = default)
    {
        var inputs = texts.ToArray();
        if (inputs.Length == 0)
        {
            return Array.Empty<float[]>();
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("OpenRouter embeddings model id is required.");
        }
        ValidateEmbeddingModelCapability(modelId, requestPresetJson);
        var baseUrl = (_configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1").TrimEnd('/');
        var apiKey = _configuration["OpenRouter:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenRouter:ApiKey is required for OpenRouter embeddings.");
        }

        var endpoint = $"{baseUrl}/embeddings";
        var requestDto = new OpenRouterEmbeddingRequest(modelId, inputs.Length == 1 ? inputs[0] : inputs);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestDto), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenRouter embeddings request failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<OpenRouterEmbeddingResponse>(body, Serializer.Options)
            ?? throw new InvalidOperationException("OpenRouter embeddings response was empty.");
        if (parsed.Data == null || parsed.Data.Count == 0)
        {
            throw new InvalidOperationException("OpenRouter embeddings response did not contain any vectors.");
        }

        return parsed.Data.Select(d => d.Embedding).ToArray();
    }

    private sealed record OpenRouterEmbeddingRequest(string Model, object Input);
    private sealed record OpenRouterEmbeddingResponse(IReadOnlyList<OpenRouterEmbeddingData> Data);
    private sealed record OpenRouterEmbeddingData(float[] Embedding);
    private static class Serializer
    {
        internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    }

    private void ValidateEmbeddingModelCapability(string modelId, string? requestPresetJson)
    {
        var configured = ReadServiceModePresetField(requestPresetJson, "AllowedModels");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (IsModelAllowed(modelId, configured))
            {
                return;
            }

            throw new InvalidOperationException(
                $"OpenRouter model '{modelId}' is not in the Embeddings service-mode AllowedModels preset.");
        }

        if (modelId.Contains("embed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"OpenRouter model '{modelId}' is not recognized as embedding-capable. " +
            "Set Embeddings service-mode AllowedModels to explicitly allow it.");
    }

    private static bool IsModelAllowed(string modelId, string allowlistCsv)
    {
        var entries = allowlistCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            if (entry.EndsWith('*'))
            {
                var prefix = entry[..^1];
                if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(modelId, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
}
