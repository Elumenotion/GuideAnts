using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal sealed class HuggingFaceEmbeddingService(
    HttpClient client,
    IConfiguration configuration) : IEmbeddingService
{
    private sealed record HfProviderRoute(string Provider, string ProviderId);

    private readonly HttpClient _client = client;
    private readonly IConfiguration _configuration = configuration;

    public Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "Hugging Face embeddings require an explicit model id from ServiceModes. " +
            "Use ProviderRoutedEmbeddingService with mode.ModelId.");
    }

    public async Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        string modelId,
        string? requestPresetJson,
        CancellationToken cancellationToken = default)
    {
        _ = requestPresetJson;

        var inputs = texts.ToArray();
        if (inputs.Length == 0)
        {
            return Array.Empty<float[]>();
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("Hugging Face embeddings model id is required.");
        }
        var token = _configuration["HuggingFace:Token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("HuggingFace:Token is required for Hugging Face embeddings.");
        }

        var route = await ResolveHuggingFaceEmbeddingRouteAsync(modelId, token, cancellationToken).ConfigureAwait(false);
        var endpoint = ResolveHuggingFaceEmbeddingsEndpoint(route);
        var requestDto = new HuggingFaceEmbeddingRequest(inputs.Length == 1 ? inputs[0] : inputs);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestDto), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Hugging Face embeddings request failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<JsonElement>(body);
        return ParseEmbeddings(parsed);
    }

    private static float[][] ParseEmbeddings(JsonElement root)
    {
        // HF feature-extraction returns either number[] for single input
        // or number[][] for batched inputs.
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var first = root[0];
            if (first.ValueKind == JsonValueKind.Number)
            {
                return [root.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()];
            }

            if (first.ValueKind == JsonValueKind.Array)
            {
                return root.EnumerateArray()
                    .Select(item => item.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray())
                    .ToArray();
            }
        }

        throw new InvalidOperationException("Hugging Face embeddings response shape was not recognized.");
    }

    private sealed record HuggingFaceEmbeddingRequest([property: JsonPropertyName("inputs")] object Inputs);

    private string ResolveHuggingFaceEmbeddingsEndpoint(HfProviderRoute route)
    {
        var configuredRouterBase = _configuration["HuggingFace:RouterBaseUrl"];
        var routerBase = string.IsNullOrWhiteSpace(configuredRouterBase)
            ? "https://router.huggingface.co/v1"
            : configuredRouterBase;

        var normalized = routerBase.TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^3];
        }

        if (string.Equals(route.Provider, "hf-inference", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/hf-inference/models/{route.ProviderId}";
        }

        return $"{normalized}/{route.Provider}/{route.ProviderId}";
    }

    private async Task<HfProviderRoute> ResolveHuggingFaceEmbeddingRouteAsync(
        string modelId,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://huggingface.co/api/models/{modelId}?expand[]=inferenceProviderMapping");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to resolve Hugging Face providers for '{modelId}': {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("inferenceProviderMapping", out var mapping)
            || mapping.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' has no inference provider mapping.");
        }

        var liveTasks = new List<string>();
        foreach (var provider in mapping.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var status = provider.Value.TryGetProperty("status", out var statusNode)
                ? statusNode.GetString()
                : null;
            var task = provider.Value.TryGetProperty("task", out var taskNode)
                ? taskNode.GetString()
                : null;
            var providerId = provider.Value.TryGetProperty("providerId", out var providerIdNode)
                ? providerIdNode.GetString()
                : null;

            if (!string.Equals(status, "live", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(task))
            {
                liveTasks.Add(task);
            }

            if (string.Equals(task, "feature-extraction", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(providerId))
            {
                return new HfProviderRoute(provider.Name, providerId);
            }
        }

        var discovered = liveTasks.Count == 0
            ? "<none>"
            : string.Join(", ", liveTasks.Distinct(StringComparer.OrdinalIgnoreCase));
        throw new InvalidOperationException(
            $"No live Hugging Face feature-extraction route found for model '{modelId}'. Live mapped task(s): {discovered}.");
    }

}
