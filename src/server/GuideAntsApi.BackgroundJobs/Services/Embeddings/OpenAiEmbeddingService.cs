using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal sealed class OpenAiEmbeddingService(
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
            "OpenAI embeddings require an explicit model id from ServiceModes. " +
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
            throw new InvalidOperationException("OpenAI embeddings model id is required.");
        }

        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI:ApiKey is required for OpenAI embeddings.");
        }

        var baseUrl = (_configuration["OpenAI:Endpoint"] ?? "https://api.openai.com/v1").TrimEnd('/');
        var endpoint = $"{baseUrl}/embeddings";

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = modelId,
            ["input"] = inputs.Length == 1 ? (object)inputs[0] : inputs
        };

        var dimensions = ReadServiceModePresetField(requestPresetJson, "Dimensions");
        if (int.TryParse(dimensions, out var dim) && dim > 0)
        {
            requestBody["dimensions"] = dim;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI embeddings request failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<OpenAiEmbeddingResponse>(body, Serializer.Options)
            ?? throw new InvalidOperationException("OpenAI embeddings response was empty.");
        if (parsed.Data == null || parsed.Data.Count == 0)
        {
            throw new InvalidOperationException("OpenAI embeddings response did not contain any vectors.");
        }

        return parsed.Data.Select(d => d.Embedding).ToArray();
    }

    private sealed record OpenAiEmbeddingResponse(IReadOnlyList<OpenAiEmbeddingData> Data);
    private sealed record OpenAiEmbeddingData(float[] Embedding);
    private static class Serializer
    {
        internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
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
