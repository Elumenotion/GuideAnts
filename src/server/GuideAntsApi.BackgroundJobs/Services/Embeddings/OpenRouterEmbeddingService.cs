using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.OpenRouter;
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
        var baseUrl = (_configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1").TrimEnd('/');
        var apiKey = _configuration["OpenRouter:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenRouter:ApiKey is required for OpenRouter embeddings.");
        }

        var endpoint = $"{baseUrl}/embeddings";
        var requestBody = BuildRequestBody(modelId, inputs, requestPresetJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        OpenRouterAttribution.Apply(request);

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

    private string BuildRequestBody(string modelId, string[] inputs, string? requestPresetJson)
    {
        var body = new JsonObject
        {
            ["model"] = modelId,
            ["input"] = inputs.Length == 1 ? inputs[0] : JsonSerializer.SerializeToNode(inputs)
        };

        if (TryReadPositiveInt(requestPresetJson, "Dimensions", out var dimensions))
        {
            body["dimensions"] = dimensions;
        }

        return body.ToJsonString();
    }

    private static bool TryReadPositiveInt(string? requestPresetJson, string fieldName, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(requestPresetJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(requestPresetJson);
            if (!document.RootElement.TryGetProperty(fieldName, out var node))
            {
                return false;
            }

            if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out value) && value > 0)
            {
                return true;
            }

            if (node.ValueKind == JsonValueKind.String
                && int.TryParse(node.GetString(), out value)
                && value > 0)
            {
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private sealed record OpenRouterEmbeddingResponse(IReadOnlyList<OpenRouterEmbeddingData> Data);
    private sealed record OpenRouterEmbeddingData(float[] Embedding);
    private static class Serializer
    {
        internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    }

}
