using System.Text.Json;
using Microsoft.Extensions.Options;
using GuideAntsApi.BackgroundJobs.Options;

namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal sealed class AzureOpenAiEmbeddingService(HttpClient client, IOptions<AzureOpenAiEmbeddingOptions> opt) : IEmbeddingService
{
    private readonly HttpClient _client = client;
    private readonly AzureOpenAiEmbeddingOptions _o = opt.Value;

    public async Task<float[][]> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        _ = purpose;
        var list = texts.ToList();
        if (list.Count == 0) return Array.Empty<float[]>();

        var endpoint = _o.Endpoint.TrimEnd('/') + $"/openai/deployments/{_o.Deployment}/embeddings?api-version=2023-05-15";
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Add("api-key", _o.ApiKey);
        var body = new { input = list };
        req.Content = new StringContent(JsonSerializer.Serialize(body));
        req.Content.Headers.ContentType = new("application/json");
        var resp = await _client.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = doc.RootElement;
        var data = root.GetProperty("data");
        var result = new float[data.GetArrayLength()][];
        int idx = 0;
        foreach (var item in data.EnumerateArray())
        {
            var emb = item.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            result[idx++] = emb;
        }
        return result;
    }
}
