using System.Text;
using System.Text.Json;
using GuideAntsApi.BackgroundJobs.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal sealed class LocalEmbeddingService(
    HttpClient client,
    IOptionsMonitor<EmbeddingsOptions> optionsMonitor,
    IOptionsMonitor<LocalServiceHostsOptions> localServiceHostsOptionsMonitor,
    ILogger<LocalEmbeddingService> logger) : IEmbeddingService
{
    private const int SourceVectorDimensions = 1024;
    private const int TargetVectorDimensions = 1536;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _client = client;
    private readonly IOptionsMonitor<EmbeddingsOptions> _optionsMonitor = optionsMonitor;
    private readonly IOptionsMonitor<LocalServiceHostsOptions> _localServiceHostsOptionsMonitor = localServiceHostsOptionsMonitor;
    private readonly ILogger<LocalEmbeddingService> _logger = logger;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _nextAllowedRequestUtc = DateTimeOffset.MinValue;

    public async Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        var list = texts.ToList();
        if (list.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var options = _optionsMonitor.CurrentValue;
        var localHosts = _localServiceHostsOptionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(localHosts.EmbeddingsBaseUrl))
        {
            throw new InvalidOperationException(
                "LocalServiceHosts:EmbeddingsBaseUrl is required for the local embeddings provider.");
        }

        var purposeValue = purpose == EmbeddingPurpose.Query ? "query" : "document";
        var endpoint = $"{localHosts.EmbeddingsBaseUrl.TrimEnd('/')}/emb/embed";
        var requestBody = new LocalEmbedRequest(list, purposeValue);
        var requestJson = JsonSerializer.Serialize(requestBody, SerializerOptions);
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var minIntervalMs = Math.Max(0, options.LocalMinIntervalMs);
            if (minIntervalMs > 0)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                if (_nextAllowedRequestUtc > nowUtc)
                {
                    var delay = _nextAllowedRequestUtc - nowUtc;
                    _logger.LogDebug(
                        "Delaying local embeddings request by {DelayMs}ms to meter GPU load.",
                        (int)delay.TotalMilliseconds);
                    await Task.Delay(delay, cancellationToken);
                }
                _nextAllowedRequestUtc = DateTimeOffset.UtcNow.AddMilliseconds(minIntervalMs);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));

            using var response = await _client.SendAsync(request, timeoutCts.Token);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Local embeddings endpoint failed ({(int)response.StatusCode}): {TruncateForLog(responseJson)}");
            }

            var parsed = JsonSerializer.Deserialize<LocalEmbedResponse>(responseJson, SerializerOptions);
            if (parsed?.Data == null)
            {
                throw new InvalidOperationException("Local embeddings response is missing data.");
            }

            if (parsed.Data.Count != list.Count)
            {
                throw new InvalidOperationException(
                    $"Local embeddings returned {parsed.Data.Count} vectors for {list.Count} inputs.");
            }

            if (parsed.Dimensions != SourceVectorDimensions)
            {
                throw new InvalidOperationException(
                    $"Local embeddings response dimensions must be {SourceVectorDimensions}, but was {parsed.Dimensions}.");
            }

            var paddedVectors = new float[parsed.Data.Count][];
            for (var i = 0; i < parsed.Data.Count; i++)
            {
                var embedding = parsed.Data[i].Embedding ?? [];
                if (embedding.Length != SourceVectorDimensions)
                {
                    throw new InvalidOperationException(
                        $"Local embeddings vector {i} has length {embedding.Length}; expected {SourceVectorDimensions}.");
                }

                var padded = new float[TargetVectorDimensions];
                Array.Copy(embedding, padded, embedding.Length);
                paddedVectors[i] = padded;
            }

            _logger.LogInformation(
                "Embedded {Count} {Purpose} inputs via local provider modelRef={ModelRef} sourceDimensions={SourceDimensions} targetDimensions={TargetDimensions} meterIntervalMs={MeterIntervalMs}",
                list.Count,
                purposeValue,
                parsed.ModelRef,
                SourceVectorDimensions,
                TargetVectorDimensions,
                minIntervalMs);

            return paddedVectors;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static string TruncateForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        const int maxChars = 2048;
        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return $"{trimmed[..maxChars]}...<truncated:{trimmed.Length - maxChars}>";
    }

    private sealed record LocalEmbedRequest(IReadOnlyList<string> Inputs, string Purpose);

    private sealed record LocalEmbedResponse(
        IReadOnlyList<LocalEmbedVector> Data,
        int Dimensions,
        string? ModelRef);

    private sealed record LocalEmbedVector(float[] Embedding);
}
