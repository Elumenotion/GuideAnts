using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Services.LlamaCpp;

public interface ILlamaServerRuntimeClient
{
    Task<LlamaModelsResponse> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<LlamaOpenAiModelsResponse> ListOpenAiModelsAsync(CancellationToken cancellationToken = default);
    Task LoadModelAsync(string modelPathOrPreset, CancellationToken cancellationToken = default);
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

    // Router mode marks a child process that exited during load with these
    // fields while status.value may still be "unloaded".
    [JsonPropertyName("failed")]
    public bool Failed { get; set; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; set; }

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
    private static readonly TimeSpan[] TransientRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(5)
    ];

    private static readonly TimeSpan ReadAttemptTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;
    private readonly ILogger<LlamaServerRuntimeClient> _logger;

    public LlamaServerRuntimeClient(HttpClient httpClient, ILogger<LlamaServerRuntimeClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<LlamaModelsResponse> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var responseContent = await GetStringWithTransientRetryAsync("models", cancellationToken);
        return JsonSerializer.Deserialize<LlamaModelsResponse>(responseContent) ?? new LlamaModelsResponse();
    }

    public async Task<LlamaOpenAiModelsResponse> ListOpenAiModelsAsync(CancellationToken cancellationToken = default)
    {
        var responseContent = await GetStringWithTransientRetryAsync("v1/models", cancellationToken);
        return JsonSerializer.Deserialize<LlamaOpenAiModelsResponse>(responseContent) ?? new LlamaOpenAiModelsResponse();
    }

    public async Task LoadModelAsync(string modelPathOrPreset, CancellationToken cancellationToken = default)
    {
        var requestBody = new JsonObject
        {
            ["model"] = modelPathOrPreset
        };

        var requestPath = "models/load";
        var requestJson = requestBody.ToJsonString();
        
        await PostJsonWithTransientRetryAsync(requestPath, requestJson, cancellationToken);
    }

    public async Task UnloadModelAsync(string routerModelId, CancellationToken cancellationToken = default)
    {
        var requestBody = new { model = routerModelId };
        var requestPath = "models/unload";
        var requestJson = JsonSerializer.Serialize(requestBody);
        
        await PostJsonWithTransientRetryAsync(requestPath, requestJson, cancellationToken);
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

    private static string LimitForException(string value)
    {
        const int maxChars = 2000;
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxChars ? value : value[..maxChars] + "...";
    }

    internal static bool IsBenignLoadConflict(string requestPath, HttpStatusCode statusCode, string responseContent)
    {
        if (!requestPath.EndsWith("models/load", StringComparison.Ordinal))
        {
            return false;
        }

        if (statusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Conflict))
        {
            return false;
        }

        return responseContent.Contains("already running", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsNonRetryableConnectionFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No address associated with hostname", StringComparison.OrdinalIgnoreCase)
                || message.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> GetStringWithTransientRetryAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildEndpointUri(_httpClient.BaseAddress, requestPath);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(ReadAttemptTimeout);
                using var response = await _httpClient.GetAsync(requestUri, attemptCts.Token);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return responseContent;
                }

                if (ShouldRetryStatus(response.StatusCode, attempt))
                {
                    await DelayBeforeRetryAsync(
                        "GET",
                        requestUri,
                        attempt,
                        $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "<none>"})",
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogError(
                    "Llama runtime GET failed. Url: {RequestUri}. Status: {StatusCode}. ResponseBody: {ResponseBody}",
                    requestUri.ToString(),
                    (int)response.StatusCode,
                    responseContent);
                throw new HttpRequestException(
                    $"Llama runtime GET {requestPath} failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "<none>"}). ResponseBody={LimitForException(responseContent)}",
                    null,
                    response.StatusCode);
            }
            catch (Exception ex) when (ShouldRetryException(ex, cancellationToken, attempt))
            {
                await DelayBeforeRetryAsync(
                    "GET",
                    requestUri,
                    attempt,
                    ex.Message,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PostJsonWithTransientRetryAsync(
        string requestPath,
        string requestJson,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildEndpointUri(_httpClient.BaseAddress, requestPath);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                };
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                if (IsBenignLoadConflict(requestPath, response.StatusCode, responseContent))
                {
                    _logger.LogDebug(
                        "Llama runtime POST {RequestPath} returned HTTP {(StatusCode)} but the model is already running; treating as success.",
                        requestPath,
                        (int)response.StatusCode);
                    return;
                }

                if (ShouldRetryStatus(response.StatusCode, attempt))
                {
                    await DelayBeforeRetryAsync(
                        "POST",
                        requestUri,
                        attempt,
                        $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "<none>"})",
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogError(
                    "Llama runtime POST failed. Url: {RequestUri}. Status: {StatusCode}. RequestBody: {RequestBody}. ResponseBody: {ResponseBody}",
                    requestUri.ToString(),
                    (int)response.StatusCode,
                    requestJson,
                    responseContent);
                throw new HttpRequestException(
                    $"Llama runtime POST {requestPath} failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "<none>"}). ResponseBody={LimitForException(responseContent)}",
                    null,
                    response.StatusCode);
            }
            catch (Exception ex) when (ShouldRetryException(ex, cancellationToken, attempt))
            {
                await DelayBeforeRetryAsync(
                    "POST",
                    requestUri,
                    attempt,
                    ex.Message,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool ShouldRetryStatus(HttpStatusCode statusCode, int attempt)
    {
        return attempt < TransientRetryDelays.Length
            && (statusCode == HttpStatusCode.RequestTimeout
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout);
    }

    private static bool ShouldRetryException(Exception ex, CancellationToken cancellationToken, int attempt)
    {
        if (attempt >= TransientRetryDelays.Length
            || cancellationToken.IsCancellationRequested
            || IsNonRetryableConnectionFailure(ex))
        {
            return false;
        }

        return ex is HttpRequestException { StatusCode: null }
            || ex is TaskCanceledException;
    }

    private async Task DelayBeforeRetryAsync(
        string method,
        Uri requestUri,
        int attempt,
        string reason,
        CancellationToken cancellationToken)
    {
        var delay = TransientRetryDelays[attempt];
        _logger.LogWarning(
            "Transient llama runtime {Method} failure. Url: {RequestUri}. Attempt: {Attempt}/{MaxAttempts}. Retrying in {DelayMs} ms. Reason: {Reason}",
            method,
            requestUri.ToString(),
            attempt + 1,
            TransientRetryDelays.Length + 1,
            (int)delay.TotalMilliseconds,
            LimitForException(reason));
        await Task.Delay(delay, cancellationToken);
    }
}
