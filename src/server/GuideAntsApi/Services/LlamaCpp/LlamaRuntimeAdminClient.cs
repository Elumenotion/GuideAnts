using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.LlamaCpp;

public sealed record LlamaAdminRouterEntryDto(
    string Alias,
    string? ModelPath,
    string? MmprojPath,
    bool HasModelFile,
    bool HasMmprojFile,
    [property: JsonPropertyName("contextSize")] int? ContextSize,
    [property: JsonPropertyName("cacheRamMib")] int? CacheRamMib);

public sealed record LlamaAdminRestartResultDto(
    [property: JsonPropertyName("restarted")] bool Restarted,
    [property: JsonPropertyName("termed")] bool Termed,
    [property: JsonPropertyName("oldPid")] int? OldPid,
    [property: JsonPropertyName("newPid")] int? NewPid);

public sealed class LlamaRuntimeAdminConflictException : Exception
{
    public LlamaRuntimeAdminConflictException(ModelDownloadOperationDto existingOperation)
        : base($"Llama admin reported a conflicting in-flight operation: {existingOperation.OperationId}")
    {
        ExistingOperation = existingOperation;
    }

    public ModelDownloadOperationDto ExistingOperation { get; }
}

public sealed record LlamaAdminStartDownloadRequest(
    string Repository,
    string QuantIncludePattern,
    string MmprojIncludePattern,
    string RouterModelId,
    string TargetDirectory,
    string? HfToken,
    bool AllowOverwrite);

public interface ILlamaRuntimeAdminClient
{
    Task<IReadOnlyList<LlamaAdminRouterEntryDto>> GetRouterEntriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates <c>model</c> / <c>mmproj</c> in <c>router-models.ini</c> and preserves per-alias
    /// context and cache-ram keys when absent from this request (used after HF download registration).</summary>
    Task AddOrUpdateRouterEntryAsync(
        string alias,
        string modelPath,
        string mmprojPath,
        CancellationToken cancellationToken = default);

    /// <summary>Reconciles full section including <c>ctx-size</c> and <c>cache-ram</c>; <c>null</c> for a
    /// value removes the corresponding key so container defaults apply.</summary>
    Task AddOrUpdateRouterEntryAsync(
        string alias,
        string modelPath,
        string mmprojPath,
        int? contextSize,
        int? cacheRamMib,
        CancellationToken cancellationToken = default);

    Task<ModelDownloadOperationDto> StartDownloadAsync(
        StartModelDownloadRequest request,
        string? resolvedHfToken,
        bool allowOverwrite,
        CancellationToken cancellationToken = default);

    Task<ModelDownloadOperationDto?> GetDownloadStatusAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a router alias from router-models.ini and deletes artifact files under the model store.
    /// Returns <c>false</c> when the alias is not registered (HTTP 404 from llama-admin).
    /// </summary>
    Task<bool> DeleteRouterEntryAsync(string alias, CancellationToken cancellationToken = default);

    /// <summary>
    /// Operator-initiated restart of llama-server. Sends SIGTERM to the current PID (no-op if
    /// already dead) and waits for entrypoint.sh to respawn it and for <c>/models</c> to answer
    /// before returning. Deliberately does not preserve or restore the loaded alias set — the
    /// crash-recovery UX re-enters the model-load dialog on return so the user re-selects which
    /// model to load.
    /// </summary>
    Task<LlamaAdminRestartResultDto> RestartLlamaServerAsync(CancellationToken cancellationToken = default);
}

public sealed class LlamaRuntimeAdminClient : ILlamaRuntimeAdminClient
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions RouterEntryUpsertJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<LlamaRuntimeAdminClient> _logger;

    public LlamaRuntimeAdminClient(HttpClient httpClient, ILogger<LlamaRuntimeAdminClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LlamaAdminRouterEntryDto>> GetRouterEntriesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("router/entries", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to fetch router entries from llama admin ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<List<LlamaAdminRouterEntryDto>>(body, DeserializeOptions);
        return parsed ?? [];
    }

    public async Task AddOrUpdateRouterEntryAsync(
        string alias,
        string modelPath,
        string mmprojPath,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["alias"] = alias,
            ["modelPath"] = modelPath,
            ["mmprojPath"] = mmprojPath
        };
        await PostRouterEntryAsync(payload, alias, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddOrUpdateRouterEntryAsync(
        string alias,
        string modelPath,
        string mmprojPath,
        int? contextSize,
        int? cacheRamMib,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["alias"] = alias,
            ["modelPath"] = modelPath,
            ["mmprojPath"] = mmprojPath,
            ["contextSize"] = contextSize,
            ["cacheRamMib"] = cacheRamMib
        };
        await PostRouterEntryAsync(payload, alias, cancellationToken).ConfigureAwait(false);
    }

    private async Task PostRouterEntryAsync(
        Dictionary<string, object?> payload,
        string alias,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, RouterEntryUpsertJsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "router/entries")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"Failed to upsert router entry '{alias}' via llama admin ({(int)response.StatusCode}): {errBody}");
    }

    public async Task<ModelDownloadOperationDto> StartDownloadAsync(
        StartModelDownloadRequest request,
        string? resolvedHfToken,
        bool allowOverwrite,
        CancellationToken cancellationToken = default)
    {
        var payload = new LlamaAdminStartDownloadRequest(
            Repository: request.Repository,
            QuantIncludePattern: request.QuantIncludePattern,
            MmprojIncludePattern: request.MmprojIncludePattern,
            RouterModelId: request.RouterModelId,
            TargetDirectory: request.TargetDirectory,
            HfToken: resolvedHfToken,
            AllowOverwrite: allowOverwrite);

        using var response = await _httpClient.PostAsJsonAsync("downloads", payload, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = TryParseConflictOperation(body, request.RouterModelId);
            if (existing is not null)
            {
                throw new LlamaRuntimeAdminConflictException(existing);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to start llama model download via llama admin ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<ModelDownloadOperationDto>(body, DeserializeOptions);
        return parsed ?? throw new InvalidOperationException("Llama admin returned an empty download operation payload.");
    }

    private static ModelDownloadOperationDto? TryParseConflictOperation(string body, string fallbackRouterModelId)
    {
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            var detail = root?["detail"] as JsonObject ?? root;
            if (detail is null)
            {
                return null;
            }

            var operationId = detail["operationId"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(operationId))
            {
                return null;
            }

            var status = detail["status"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(status))
            {
                status = "queued";
            }

            var routerModelId = detail["routerModelId"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(routerModelId))
            {
                routerModelId = fallbackRouterModelId?.Trim();
            }
            if (string.IsNullOrWhiteSpace(routerModelId))
            {
                routerModelId = string.Empty;
            }

            var progress = detail["progress"] is JsonValue progressNode
                && progressNode.TryGetValue<double>(out var parsedProgress)
                    ? parsedProgress
                    : (double?)null;

            var message = detail["error"]?.GetValue<string>()?.Trim();
            return new ModelDownloadOperationDto(
                OperationId: operationId,
                Status: status,
                RouterModelId: routerModelId,
                Progress: progress,
                ErrorMessage: message,
                LogLine: message);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ModelDownloadOperationDto?> GetDownloadStatusAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pollTimeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var response = await _httpClient.GetAsync(
                $"downloads/{Uri.EscapeDataString(operationId)}", pollTimeout.Token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Failed to read llama admin download status for {OperationId}. Status={StatusCode} Body={Body}",
                operationId,
                (int)response.StatusCode,
                body);
            throw new InvalidOperationException(
                $"Failed to read llama model download status via llama admin ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<ModelDownloadOperationDto>(body, DeserializeOptions);
        return parsed;
    }

    public async Task<LlamaAdminRestartResultDto> RestartLlamaServerAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "llama/restart");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.GatewayTimeout)
        {
            // Admin surfaces 504 when llama-server didn't respawn or /models didn't answer inside
            // the restart timeout. Bubble that up as a TimeoutException so the API endpoint can
            // map it to an HTTP 504 for the UI's "try again" affordance.
            _logger.LogError("Llama admin restart timed out. Body={Body}", body);
            throw new TimeoutException(
                $"Llama admin restart timed out: {body}");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Failed to restart llama-server via llama admin. Status={StatusCode}. Body={Body}",
                (int)response.StatusCode,
                body);
            throw new InvalidOperationException(
                $"Failed to restart llama-server via llama admin ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<LlamaAdminRestartResultDto>(body, DeserializeOptions);
        return parsed ?? throw new InvalidOperationException("Llama admin returned an empty restart payload.");
    }

    public async Task<bool> DeleteRouterEntryAsync(string alias, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Alias is required.", nameof(alias));
        }

        var trimmed = alias.Trim();
        var escaped = Uri.EscapeDataString(trimmed);
        using var response = await _httpClient.DeleteAsync($"router/entries/{escaped}", cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"Failed to delete router alias '{trimmed}' via llama admin ({(int)response.StatusCode}): {body}");
    }
}
