using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal sealed class GoogleGeminiEmbeddingService(
    HttpClient client,
    IConfiguration configuration) : IEmbeddingService
{
    private const int TargetVectorDimensions = 1536;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _client = client;
    private readonly IConfiguration _configuration = configuration;

    public Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "Google Gemini embeddings require an explicit model id from ServiceModes. " +
            "Use ProviderRoutedEmbeddingService with mode.ModelId.");
    }

    public Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        return GetEmbeddingsInternalAsync(texts, purpose, modelId, cancellationToken);
    }

    public async Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        return await GetEmbeddingsInternalAsync(texts, EmbeddingPurpose.Document, modelId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<float[][]> GetEmbeddingsInternalAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        string modelId,
        CancellationToken cancellationToken)
    {
        var inputs = texts.ToArray();
        if (inputs.Length == 0)
        {
            return Array.Empty<float[]>();
        }

        var apiKey = _configuration["GoogleGeminiApi:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("GoogleGeminiApi:ApiKey is required.");
        }

        var normalizedModelId = NormalizeModelName(modelId);
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/{normalizedModelId}:batchEmbedContents";
        var requestDto = new GoogleGeminiBatchEmbedContentsRequest(
            Requests: inputs.Select(text => new GoogleGeminiEmbedContentRequest(
                Model: normalizedModelId,
                Content: new GoogleGeminiContent(
                    Parts:
                    [
                        new GoogleGeminiPart(Text: text)
                    ]),
                EmbedContentConfig: new GoogleGeminiEmbedContentConfig(
                    TaskType: ToTaskType(purpose),
                    OutputDimensionality: TargetVectorDimensions))).ToArray());

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestDto, SerializerOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", apiKey);

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Gemini embeddings request failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<GoogleGeminiBatchEmbedContentsResponse>(body, SerializerOptions)
            ?? throw new InvalidOperationException("Google Gemini embeddings response was empty.");
        if (parsed.Embeddings == null || parsed.Embeddings.Count == 0)
        {
            throw new InvalidOperationException("Google Gemini embeddings response did not contain embeddings.");
        }

        var vectors = parsed.Embeddings.Select(p => p.Values).ToArray();
        if (vectors.Length != inputs.Length)
        {
            throw new InvalidOperationException(
                $"Google Gemini embeddings returned {vectors.Length} vectors for {inputs.Length} inputs.");
        }

        for (var i = 0; i < vectors.Length; i++)
        {
            if (vectors[i].Length != TargetVectorDimensions)
            {
                throw new InvalidOperationException(
                    $"Google Gemini embeddings vector {i} has length {vectors[i].Length}; expected {TargetVectorDimensions}.");
            }
        }

        return vectors;
    }

    private static string NormalizeModelName(string modelId)
    {
        var trimmed = modelId.Trim();
        return trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"models/{trimmed}";
    }

    private static string ToTaskType(EmbeddingPurpose purpose) => purpose switch
    {
        EmbeddingPurpose.Query => "RETRIEVAL_QUERY",
        _ => "RETRIEVAL_DOCUMENT"
    };

    private sealed record GoogleGeminiBatchEmbedContentsRequest(
        IReadOnlyList<GoogleGeminiEmbedContentRequest> Requests);

    private sealed record GoogleGeminiEmbedContentRequest(
        string Model,
        GoogleGeminiContent Content,
        GoogleGeminiEmbedContentConfig EmbedContentConfig);

    private sealed record GoogleGeminiContent(
        IReadOnlyList<GoogleGeminiPart> Parts);

    private sealed record GoogleGeminiPart(string Text);

    private sealed record GoogleGeminiEmbedContentConfig(
        string TaskType,
        int OutputDimensionality);

    private sealed record GoogleGeminiBatchEmbedContentsResponse(
        IReadOnlyList<GoogleGeminiContentEmbedding> Embeddings);

    private sealed record GoogleGeminiContentEmbedding(float[] Values);
}
