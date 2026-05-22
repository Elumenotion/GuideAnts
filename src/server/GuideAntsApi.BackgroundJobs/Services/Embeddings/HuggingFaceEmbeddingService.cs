using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal sealed class HuggingFaceEmbeddingService(
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
            "Hugging Face embeddings require an explicit model id from ServiceModes. " +
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
            throw new InvalidOperationException("Hugging Face embeddings model id is required.");
        }

        var token = _configuration["HuggingFace:Token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("HuggingFace:Token is required for Hugging Face embeddings.");
        }

        var endpoint = $"https://api-inference.huggingface.co/models/{modelId}";
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

    private sealed record HuggingFaceEmbeddingRequest(object Inputs);

}
