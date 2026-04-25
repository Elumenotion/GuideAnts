using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Services.LlamaCpp;

public interface ILlamaServerRuntimeClient
{
    Task<LlamaModelsResponse> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<LlamaOpenAiModelsResponse> ListOpenAiModelsAsync(CancellationToken cancellationToken = default);
    Task LoadModelAsync(string modelPathOrPreset, JsonObject? loadParams = null, CancellationToken cancellationToken = default);
    Task UnloadModelAsync(string routerModelId, CancellationToken cancellationToken = default);
}

public class LlamaModelsResponse
{
    [JsonPropertyName("data")]
    public List<LlamaModelData> Data { get; set; } = new();
}

public class LlamaModelData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    // Router mode payload uses "status.value" (loaded/loading/unloaded).
    [JsonPropertyName("status")]
    public LlamaModelStatus? Status { get; set; }

    // Kept for compatibility with older payloads that used a flat state field.
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("meta")]
    public LlamaModelMeta? Meta { get; set; }
}

public class LlamaModelStatus
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public class LlamaModelMeta
{
    [JsonPropertyName("n_ctx_train")]
    public int? NCtxTrain { get; set; }
}

public class LlamaOpenAiModelsResponse
{
    [JsonPropertyName("data")]
    public List<LlamaOpenAiModelData> Data { get; set; } = new();
}

public class LlamaOpenAiModelData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = string.Empty;
}

public class LlamaServerRuntimeClient : ILlamaServerRuntimeClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LlamaServerRuntimeClient> _logger;

    public LlamaServerRuntimeClient(HttpClient httpClient, ILogger<LlamaServerRuntimeClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<LlamaModelsResponse> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var requestPath = "models";
        var requestUri = BuildEndpointUri(_httpClient.BaseAddress, requestPath);
        
        var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<LlamaModelsResponse>(responseContent) ?? new LlamaModelsResponse();
    }

    public async Task<LlamaOpenAiModelsResponse> ListOpenAiModelsAsync(CancellationToken cancellationToken = default)
    {
        var requestPath = "v1/models";
        var requestUri = BuildEndpointUri(_httpClient.BaseAddress, requestPath);
        
        var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<LlamaOpenAiModelsResponse>(responseContent) ?? new LlamaOpenAiModelsResponse();
    }

    public async Task LoadModelAsync(string modelPathOrPreset, JsonObject? loadParams = null, CancellationToken cancellationToken = default)
    {
        var requestBody = new JsonObject
        {
            ["model"] = modelPathOrPreset
        };

        if (loadParams is not null)
        {
            foreach (var kvp in loadParams)
            {
                if (!string.Equals(kvp.Key, "model", StringComparison.Ordinal))
                {
                    requestBody[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
        }

        var requestPath = "models/load";
        var requestUri = BuildEndpointUri(_httpClient.BaseAddress, requestPath);
        var requestJson = requestBody.ToJsonString();
        
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Llama runtime POST failed. Url: {RequestUri}. Status: {StatusCode}. RequestBody: {RequestBody}. ResponseBody: {ResponseBody}",
                requestUri.ToString(),
                (int)response.StatusCode,
                requestJson,
                responseContent);
        }
        response.EnsureSuccessStatusCode();
    }

    public async Task UnloadModelAsync(string routerModelId, CancellationToken cancellationToken = default)
    {
        var requestBody = new { model = routerModelId };
        var requestPath = "models/unload";
        var requestUri = BuildEndpointUri(_httpClient.BaseAddress, requestPath);
        var requestJson = JsonSerializer.Serialize(requestBody);
        
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Llama runtime POST failed. Url: {RequestUri}. Status: {StatusCode}. RequestBody: {RequestBody}. ResponseBody: {ResponseBody}",
                requestUri.ToString(),
                (int)response.StatusCode,
                requestJson,
                responseContent);
        }
        response.EnsureSuccessStatusCode();
    }

    internal static Uri BuildEndpointUri(Uri? baseAddress, string relativePath)
    {
        if (baseAddress is null)
        {
            throw new InvalidOperationException("Llama runtime HttpClient BaseAddress must be configured.");
        }

        var normalizedBaseUrl = baseAddress.AbsoluteUri.TrimEnd('/') + "/";
        var normalizedRelativePath = relativePath.TrimStart('/');
        return new Uri(new Uri(normalizedBaseUrl), normalizedRelativePath);
    }
}
