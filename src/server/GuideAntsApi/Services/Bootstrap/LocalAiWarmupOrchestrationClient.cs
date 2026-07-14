using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Services.Bootstrap;

public interface ILocalAiWarmupOrchestrationClient
{
    Task<WarmupDesiredWriteResult> PutDesiredAsync(
        string iniText,
        int? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task<WarmupApplyResult> ApplyAsync(CancellationToken cancellationToken = default);

    Task<WarmupStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalAiWarmupOrchestrationClient : ILocalAiWarmupOrchestrationClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalAiWarmupOrchestrationClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public LocalAiWarmupOrchestrationClient(
        HttpClient httpClient,
        ILogger<LocalAiWarmupOrchestrationClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<WarmupDesiredWriteResult> PutDesiredAsync(
        string iniText,
        int? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        var requestUri = "warmup/desired";
        if (expectedRevision.HasValue)
        {
            requestUri += $"?expected_revision={expectedRevision.Value}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = new StringContent(iniText, Encoding.UTF8, "text/plain"),
        };

        if (expectedRevision.HasValue)
        {
            request.Headers.TryAddWithoutValidation("If-Match-Revision", expectedRevision.Value.ToString());
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Warmup desired PUT failed ({StatusCode}): {Body}",
                (int)response.StatusCode,
                Truncate(body, 512));
            response.EnsureSuccessStatusCode();
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new WarmupDesiredWriteResult(
            Revision: root.GetProperty("revision").GetInt32(),
            Sha256: root.GetProperty("sha256").GetString() ?? string.Empty,
            Changed: root.GetProperty("changed").GetBoolean());
    }

    public async Task<WarmupApplyResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync("warmup/apply", content: null, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Warmup apply failed ({StatusCode}): {Body}",
                (int)response.StatusCode,
                Truncate(body, 512));
            response.EnsureSuccessStatusCode();
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new WarmupApplyResult(
            Ok: root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
            Noop: root.TryGetProperty("noop", out var noop) && noop.GetBoolean(),
            Continue: root.TryGetProperty("continue", out var cont) && cont.GetBoolean(),
            Started: root.TryGetProperty("started", out var started) && started.GetBoolean(),
            DesiredRevision: root.TryGetProperty("desiredRevision", out var desired) ? desired.GetInt32() : 0,
            AppliedRevision: root.TryGetProperty("appliedRevision", out var applied) ? applied.GetInt32() : 0,
            ApplyStatus: root.TryGetProperty("applyStatus", out var status)
                ? status.GetString() ?? "unknown"
                : "unknown");
    }

    public async Task<WarmupStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("warmup/status", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Warmup status GET failed ({StatusCode}): {Body}",
                (int)response.StatusCode,
                Truncate(body, 512));
            response.EnsureSuccessStatusCode();
        }

        var parsed = JsonSerializer.Deserialize<WarmupStatusWireDto>(body, JsonOptions)
            ?? throw new InvalidOperationException("Warmup status response was empty.");

        var services = new Dictionary<string, WarmupServiceStatus>(StringComparer.Ordinal);
        if (parsed.Services is not null)
        {
            foreach (var (serviceId, entry) in parsed.Services)
            {
                services[serviceId] = new WarmupServiceStatus(
                    Desired: entry.Desired ?? "idle",
                    Applied: entry.Applied ?? "idle",
                    Phase: entry.Phase ?? "idle",
                    Error: entry.Error,
                    RouterAlias: entry.RouterAlias,
                    ModelId: entry.ModelId,
                    BundleId: entry.BundleId);
            }
        }

        return new WarmupStatusDocument(
            SchemaVersion: parsed.SchemaVersion,
            DesiredRevision: parsed.DesiredRevision,
            AppliedRevision: parsed.AppliedRevision,
            InProgressRevision: parsed.InProgressRevision,
            ApplyStatus: parsed.ApplyStatus ?? "idle",
            ApplyError: parsed.ApplyError,
            DesiredSha256: parsed.DesiredSha256 ?? string.Empty,
            WrittenAt: parsed.WrittenAt ?? string.Empty,
            Services: services);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed class WarmupStatusWireDto
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("desiredRevision")]
        public int DesiredRevision { get; set; }

        [JsonPropertyName("appliedRevision")]
        public int AppliedRevision { get; set; }

        [JsonPropertyName("inProgressRevision")]
        public int? InProgressRevision { get; set; }

        [JsonPropertyName("applyStatus")]
        public string? ApplyStatus { get; set; }

        [JsonPropertyName("applyError")]
        public string? ApplyError { get; set; }

        [JsonPropertyName("desiredSha256")]
        public string? DesiredSha256 { get; set; }

        [JsonPropertyName("writtenAt")]
        public string? WrittenAt { get; set; }

        [JsonPropertyName("services")]
        public Dictionary<string, WarmupServiceWireDto>? Services { get; set; }
    }

    private sealed class WarmupServiceWireDto
    {
        [JsonPropertyName("desired")]
        public string? Desired { get; set; }

        [JsonPropertyName("applied")]
        public string? Applied { get; set; }

        [JsonPropertyName("phase")]
        public string? Phase { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("routerAlias")]
        public string? RouterAlias { get; set; }

        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }

        [JsonPropertyName("bundleId")]
        public string? BundleId { get; set; }
    }
}
