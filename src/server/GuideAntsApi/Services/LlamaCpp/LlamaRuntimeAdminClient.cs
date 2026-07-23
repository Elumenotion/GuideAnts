using System.Net;
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
    [property: JsonPropertyName("cacheRamMib")] int? CacheRamMib,
    IReadOnlyDictionary<string, string>? Preset);

public sealed record LlamaAdminRouterEntriesResponseDto(
    [property: JsonPropertyName("entries")] IReadOnlyList<LlamaAdminRouterEntryDto> Entries);

public sealed record LlamaAdminRouterEntryUpsertRequest(
    string Alias,
    string ModelPath,
    string MmprojPath,
    IReadOnlyDictionary<string, string>? Preset,
    string PresetMode = "replace",
    int? ContextSize = null,
    int? CacheRamMib = null);

public sealed record LlamaAdminExactDownloadRequest(
    string OperationId,
    string Repository,
    string ResolvedRevision,
    IReadOnlyList<string> ModelFiles,
    IReadOnlyList<string> MmprojFiles,
    string Alias,
    string TargetDirectory,
    IReadOnlyDictionary<string, string> Preset,
    string PresetMode,
    IReadOnlyList<LlamaArtifactMetadataDto>? ArtifactMetadata,
    string? HfToken);

public sealed record LlamaAdminRouterEntryUpsertResult(
    bool Ok,
    string? IniSha256,
    LlamaAdminRuntimeApplyDto? RuntimeApply);

public sealed record LlamaAdminRuntimeApplyDto(
    bool Applied,
    string IniSha256,
    string? Remediation);

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
    Task<LlamaCatalogResponseDto> GetCatalogAsync(CancellationToken cancellationToken = default);

    Task<LlamaCatalogQuantsResponseDto> GetCatalogQuantsAsync(
        string catalogId,
        string? catalogVersion,
        string? resolvedHfToken,
        CancellationToken cancellationToken = default,
        string? resolvedRevision = null);

    Task<LlamaAdminRouterEntriesResponseDto> GetRouterEntriesAsync(CancellationToken cancellationToken = default);

    Task<LlamaAdminRouterEntryUpsertResult> PutRouterEntryAsync(
        LlamaRouterEntryPutRequest request,
        CancellationToken cancellationToken = default);

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

    Task<ModelDownloadOperationDto> StartExactDownloadAsync(
        ExactStartModelDownloadRequest request,
        string? resolvedHfToken,
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

    Task DeleteObsoleteArtifactPathsAsync(
        string targetDirectory,
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken = default);

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

    public async Task<LlamaCatalogResponseDto> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("admin/catalog", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to fetch llama catalog from llama admin ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<LlamaCatalogResponseDto>(body, DeserializeOptions);
        return parsed ?? throw new InvalidOperationException("Llama admin returned an empty catalog payload.");
    }

    public async Task<LlamaCatalogQuantsResponseDto> GetCatalogQuantsAsync(
        string catalogId,
        string? catalogVersion,
        string? resolvedHfToken,
        CancellationToken cancellationToken = default,
        string? resolvedRevision = null)
    {
        if (string.IsNullOrWhiteSpace(catalogId))
        {
            throw new ArgumentException("Catalog id is required.", nameof(catalogId));
        }

        var escapedId = Uri.EscapeDataString(catalogId.Trim());
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(catalogVersion))
        {
            query.Add($"catalogVersion={Uri.EscapeDataString(catalogVersion.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(resolvedRevision))
        {
            query.Add($"resolvedRevision={Uri.EscapeDataString(resolvedRevision.Trim())}");
        }

        var path = $"admin/catalog/{escapedId}/quants";
        if (query.Count > 0)
        {
            path += "?" + string.Join("&", query);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(resolvedHfToken))
        {
            request.Headers.TryAddWithoutValidation("X-HF-Token", resolvedHfToken.Trim());
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateCatalogServiceException(response.StatusCode, body);
        }

        var parsed = JsonSerializer.Deserialize<LlamaCatalogQuantsResponseDto>(body, DeserializeOptions);
        return parsed ?? throw new InvalidOperationException("Llama admin returned an empty quants payload.");
    }

    private static LlamaCatalogServiceException CreateCatalogServiceException(HttpStatusCode statusCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement detail = root;
            if (root.TryGetProperty("detail", out var detailNode))
            {
                detail = detailNode;
            }

            var code = detail.TryGetProperty("code", out var codeNode)
                ? codeNode.GetString() ?? "LLAMA_CATALOG_ERROR"
                : "LLAMA_CATALOG_ERROR";
            var message = detail.TryGetProperty("message", out var messageNode)
                ? messageNode.GetString() ?? "Llama catalog request failed."
                : root.TryGetProperty("detail", out var rawDetail) && rawDetail.ValueKind == JsonValueKind.String
                    ? rawDetail.GetString() ?? "Llama catalog request failed."
                    : "Llama catalog request failed.";
            return new LlamaCatalogServiceException(code, message, (int)statusCode);
        }
        catch (JsonException)
        {
            return new LlamaCatalogServiceException(
                "LLAMA_CATALOG_ERROR",
                $"Llama catalog request failed ({(int)statusCode}).",
                (int)statusCode);
        }
    }

    public async Task<LlamaAdminRouterEntriesResponseDto> GetRouterEntriesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("router/entries", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to fetch router entries from llama admin ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<LlamaAdminRouterEntriesResponseDto>(body, DeserializeOptions);
        return parsed ?? new LlamaAdminRouterEntriesResponseDto([]);
    }

    public async Task<LlamaAdminRouterEntryUpsertResult> PutRouterEntryAsync(
        LlamaRouterEntryPutRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = new LlamaAdminRouterEntryUpsertRequest(
            Alias: request.Alias,
            ModelPath: request.ModelPath,
            MmprojPath: request.MmprojPath,
            Preset: request.Preset,
            PresetMode: request.PresetMode,
            ContextSize: request.ContextSize,
            CacheRamMib: request.CacheRamMib);

        var json = JsonSerializer.Serialize(payload, RouterEntryUpsertJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "router/entries")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.BadGateway)
        {
            var parsedFailure = TryParseRouterUpsertResult(body);
            if (parsedFailure is not null)
            {
                return parsedFailure;
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to upsert router entry '{request.Alias}' via llama admin ({(int)response.StatusCode}): {body}");
        }

        return TryParseRouterUpsertResult(body)
            ?? new LlamaAdminRouterEntryUpsertResult(true, null, null);
    }

    private static LlamaAdminRouterEntryUpsertResult? TryParseRouterUpsertResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement payload = root;
            if (root.TryGetProperty("detail", out var detailNode) && detailNode.ValueKind == JsonValueKind.Object)
            {
                payload = detailNode;
            }

            var ok = !payload.TryGetProperty("ok", out var okNode) || okNode.GetBoolean();
            var iniSha256 = payload.TryGetProperty("iniSha256", out var iniNode)
                ? iniNode.GetString()
                : null;
            LlamaAdminRuntimeApplyDto? runtimeApply = null;
            if (payload.TryGetProperty("runtimeApply", out var runtimeNode) && runtimeNode.ValueKind == JsonValueKind.Object)
            {
                runtimeApply = new LlamaAdminRuntimeApplyDto(
                    Applied: runtimeNode.TryGetProperty("applied", out var appliedNode) && appliedNode.GetBoolean(),
                    IniSha256: runtimeNode.TryGetProperty("iniSha256", out var runtimeIniNode)
                        ? runtimeIniNode.GetString() ?? string.Empty
                        : string.Empty,
                    Remediation: runtimeNode.TryGetProperty("remediation", out var remediationNode)
                        ? remediationNode.GetString()
                        : null);
            }

            return new LlamaAdminRouterEntryUpsertResult(ok, iniSha256, runtimeApply);
        }
        catch (JsonException)
        {
            return null;
        }
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
            ["mmprojPath"] = mmprojPath,
            // This call intentionally patches model/mmproj while preserving
            // existing per-alias extras in router-models.ini.
            ["presetMode"] = "merge",
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
            // Context/cache updates are patch operations over existing alias
            // extras; merge mode prevents replacing unrelated keys.
            ["presetMode"] = "merge",
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

    public async Task<ModelDownloadOperationDto> StartExactDownloadAsync(
        ExactStartModelDownloadRequest request,
        string? resolvedHfToken,
        CancellationToken cancellationToken = default)
    {
        var payload = new LlamaAdminExactDownloadRequest(
            OperationId: request.OperationId,
            Repository: request.Repository,
            ResolvedRevision: request.ResolvedRevision,
            ModelFiles: request.ModelFiles,
            MmprojFiles: request.MmprojFiles,
            Alias: request.Alias,
            TargetDirectory: request.TargetDirectory,
            Preset: request.Preset,
            PresetMode: request.PresetMode,
            ArtifactMetadata: request.ArtifactMetadata,
            HfToken: resolvedHfToken);

        using var response = await _httpClient.PostAsJsonAsync("downloads", payload, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = TryParseConflictOperation(body, request.Alias);
            if (existing is not null)
            {
                throw new LlamaRuntimeAdminConflictException(existing);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to start exact llama model download via llama admin ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<ModelDownloadOperationDto>(body, DeserializeOptions);
        return parsed ?? throw new InvalidOperationException("Llama admin returned an empty download operation payload.");
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
                LogValueSanitizer.Sanitize(operationId),
                (int)response.StatusCode,
                LogValueSanitizer.Sanitize(body));
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
            _logger.LogError("Llama admin restart timed out. Body={Body}", LogValueSanitizer.Sanitize(body));
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

    public async Task DeleteObsoleteArtifactPathsAsync(
        string targetDirectory,
        IReadOnlyList<string> repositoryPaths,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Target directory is required.", nameof(targetDirectory));
        }

        var payload = new
        {
            targetDirectory = targetDirectory.Trim(),
            repositoryPaths = repositoryPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList(),
        };

        using var response = await _httpClient
            .PostAsJsonAsync("admin/artifacts/delete-obsolete", payload, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Failed to delete obsolete artifacts via llama admin ({(int)response.StatusCode}): {body}");
        }
    }

}
