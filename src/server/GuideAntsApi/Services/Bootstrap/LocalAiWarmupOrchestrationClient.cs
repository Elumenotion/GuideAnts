using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Services.Bootstrap;

public interface ILocalAiWarmupOrchestrationClient
{
    Task<WarmupApplyResult> ApplyAsync(
        string planJson,
        CancellationToken cancellationToken = default);

    Task<WarmupStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalAiWarmupOrchestrationClient : ILocalAiWarmupOrchestrationClient
{
    public const string HttpClientName = "LocalAiWarmupOrchestration";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalAiStackHostResolver _stackHostResolver;
    private readonly LocalAiWarmupPlanSplitter _planSplitter;
    private readonly ILogger<LocalAiWarmupOrchestrationClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public LocalAiWarmupOrchestrationClient(
        IHttpClientFactory httpClientFactory,
        ILocalAiStackHostResolver stackHostResolver,
        LocalAiWarmupPlanSplitter planSplitter,
        ILogger<LocalAiWarmupOrchestrationClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _stackHostResolver = stackHostResolver;
        _planSplitter = planSplitter;
        _logger = logger;
    }

    public async Task<WarmupApplyResult> ApplyAsync(
        string planJson,
        CancellationToken cancellationToken = default)
    {
        var stackPlans = _planSplitter.Split(planJson);
        if (stackPlans.Count == 0)
        {
            _logger.LogDebug("Skipping warmup apply: no configured local AI stack hosts.");
            return new WarmupApplyResult(
                Ok: true,
                Noop: true,
                Continue: false,
                Started: false,
                DesiredRevision: 0,
                AppliedRevision: 0,
                ApplyStatus: "idle",
                Changed: false);
        }

        var results = new List<WarmupApplyResult>(stackPlans.Count);
        foreach (var stackPlan in stackPlans)
        {
            results.Add(await ApplyToStackAsync(stackPlan, cancellationToken).ConfigureAwait(false));
        }

        return MergeApplyResults(results);
    }

    public async Task<WarmupStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var stacks = _stackHostResolver.GetAllConfiguredStackBases();
        if (stacks.Count == 0)
        {
            return EmptyStatusDocument();
        }

        var mergedServices = new Dictionary<string, WarmupServiceStatus>(StringComparer.Ordinal);
        var stackStatuses = new List<WarmupStatusDocument>(stacks.Count);
        foreach (var stackBase in stacks)
        {
            var status = await GetStatusForStackAsync(stackBase, cancellationToken).ConfigureAwait(false);
            stackStatuses.Add(status);
            foreach (var serviceId in LocalAiStackHostUrls.WarmupServiceIds)
            {
                var serviceStack = _stackHostResolver.GetStackBaseForService(serviceId);
                if (serviceStack is not null
                    && string.Equals(serviceStack, stackBase, StringComparison.OrdinalIgnoreCase)
                    && status.Services.TryGetValue(serviceId, out var serviceStatus))
                {
                    mergedServices[serviceId] = serviceStatus;
                }
            }
        }

        return MergeStackStatuses(stackStatuses, mergedServices);
    }

    private async Task<WarmupApplyResult> ApplyToStackAsync(
        StackWarmupPlan stackPlan,
        CancellationToken cancellationToken)
    {
        var adminBase = LocalAiStackHostUrls.DeriveAdminBaseUri(stackPlan.StackBaseUrl);
        var applyUri = new Uri(adminBase, "warmup/apply");
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var content = new StringContent(stackPlan.PlanJson, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(applyUri, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Warmup apply failed for stack {StackBase} ({StatusCode}): {Body}",
                stackPlan.StackBaseUrl,
                (int)response.StatusCode,
                Truncate(body, 512));
            response.EnsureSuccessStatusCode();
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        _logger.LogInformation(
            "Submitted lifecycle plan to stack {StackBase} ga-admin.",
            stackPlan.StackBaseUrl);

        return new WarmupApplyResult(
            Ok: root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
            Noop: root.TryGetProperty("noop", out var noop) && noop.GetBoolean(),
            Continue: root.TryGetProperty("continue", out var cont) && cont.GetBoolean(),
            Started: root.TryGetProperty("started", out var started) && started.GetBoolean(),
            DesiredRevision: root.TryGetProperty("desiredRevision", out var desired) ? desired.GetInt32() : 0,
            AppliedRevision: root.TryGetProperty("appliedRevision", out var applied) ? applied.GetInt32() : 0,
            ApplyStatus: root.TryGetProperty("applyStatus", out var status)
                ? status.GetString() ?? "unknown"
                : "unknown",
            Changed: root.TryGetProperty("changed", out var changed) && changed.GetBoolean());
    }

    private async Task<WarmupStatusDocument> GetStatusForStackAsync(
        string stackBase,
        CancellationToken cancellationToken)
    {
        var adminBase = LocalAiStackHostUrls.DeriveAdminBaseUri(stackBase);
        var statusUri = new Uri(adminBase, "warmup/status");
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var response = await client.GetAsync(statusUri, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Warmup status GET failed for stack {StackBase} ({StatusCode}): {Body}",
                stackBase,
                (int)response.StatusCode,
                Truncate(body, 512));
            response.EnsureSuccessStatusCode();
        }

        return ParseStatusDocument(body);
    }

    private static WarmupStatusDocument ParseStatusDocument(string body)
    {
        var parsed = JsonSerializer.Deserialize<WarmupStatusWireDto>(body, JsonOptions)
            ?? throw new InvalidOperationException("Warmup status response was empty.");

        var services = new Dictionary<string, WarmupServiceStatus>(StringComparer.Ordinal);
        if (parsed.Services is not null)
        {
            foreach (var (serviceId, entry) in parsed.Services)
            {
                var planRef = entry.PlanRef?.Trim();
                var loadedRef = entry.RouterAlias?.Trim()
                    ?? entry.ModelId?.Trim()
                    ?? entry.BundleId?.Trim();
                var phase = entry.Phase ?? "idle";
                var hasPlan = !string.IsNullOrWhiteSpace(planRef);
                var isReady = string.Equals(phase, "ready", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(loadedRef);

                services[serviceId] = new WarmupServiceStatus(
                    Desired: hasPlan ? "on" : "off",
                    Applied: isReady ? "on" : "off",
                    Phase: phase,
                    Error: entry.Error,
                    PlanRef: planRef,
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

    private static WarmupStatusDocument MergeStackStatuses(
        IReadOnlyList<WarmupStatusDocument> stackStatuses,
        Dictionary<string, WarmupServiceStatus> mergedServices)
    {
        if (stackStatuses.Count == 1)
        {
            var only = stackStatuses[0];
            return new WarmupStatusDocument(
                only.SchemaVersion,
                only.DesiredRevision,
                only.AppliedRevision,
                only.InProgressRevision,
                only.ApplyStatus,
                only.ApplyError,
                only.DesiredSha256,
                only.WrittenAt,
                mergedServices);
        }

        var applyStatus = "idle";
        string? applyError = null;
        if (stackStatuses.Any(static s =>
                string.Equals(s.ApplyStatus, "failed", StringComparison.OrdinalIgnoreCase)))
        {
            applyStatus = "failed";
            applyError = stackStatuses
                .Where(static s => string.Equals(s.ApplyStatus, "failed", StringComparison.OrdinalIgnoreCase))
                .Select(static s => s.ApplyError)
                .FirstOrDefault(static e => !string.IsNullOrWhiteSpace(e));
        }
        else if (stackStatuses.Any(static s =>
                     string.Equals(s.ApplyStatus, "applying", StringComparison.OrdinalIgnoreCase)))
        {
            applyStatus = "applying";
        }
        else if (stackStatuses.Any(static s =>
                     string.Equals(s.ApplyStatus, "pending", StringComparison.OrdinalIgnoreCase)))
        {
            applyStatus = "pending";
        }
        else if (stackStatuses.All(static s =>
                     string.Equals(s.ApplyStatus, "applied", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(s.ApplyStatus, "idle", StringComparison.OrdinalIgnoreCase)))
        {
            applyStatus = stackStatuses.All(static s =>
                string.Equals(s.ApplyStatus, "applied", StringComparison.OrdinalIgnoreCase))
                ? "applied"
                : "idle";
        }

        var allApplied = stackStatuses.All(static s => s.DesiredRevision <= s.AppliedRevision);
        return new WarmupStatusDocument(
            SchemaVersion: 1,
            DesiredRevision: stackStatuses.Max(static s => s.DesiredRevision),
            AppliedRevision: allApplied ? stackStatuses.Min(static s => s.AppliedRevision) : stackStatuses.Max(static s => s.AppliedRevision),
            InProgressRevision: stackStatuses.Select(static s => s.InProgressRevision).FirstOrDefault(static r => r.HasValue),
            ApplyStatus: applyStatus,
            ApplyError: applyError,
            DesiredSha256: string.Empty,
            WrittenAt: string.Empty,
            Services: mergedServices);
    }

    private static WarmupApplyResult MergeApplyResults(IReadOnlyList<WarmupApplyResult> results)
    {
        if (results.Count == 1)
        {
            return results[0];
        }

        return new WarmupApplyResult(
            Ok: results.All(static r => r.Ok),
            Noop: results.All(static r => r.Noop),
            Continue: results.Any(static r => r.Continue),
            Started: results.Any(static r => r.Started),
            DesiredRevision: results.Max(static r => r.DesiredRevision),
            AppliedRevision: results.Min(static r => r.AppliedRevision),
            ApplyStatus: results.Any(static r => string.Equals(r.ApplyStatus, "failed", StringComparison.OrdinalIgnoreCase))
                ? "failed"
                : results.Any(static r => string.Equals(r.ApplyStatus, "applying", StringComparison.OrdinalIgnoreCase))
                    ? "applying"
                    : results.All(static r => r.Noop)
                        ? "idle"
                        : "applied",
            Changed: results.Any(static r => r.Changed));
    }

    private static WarmupStatusDocument EmptyStatusDocument() =>
        new(
            SchemaVersion: 1,
            DesiredRevision: 0,
            AppliedRevision: 0,
            InProgressRevision: null,
            ApplyStatus: "idle",
            ApplyError: null,
            DesiredSha256: string.Empty,
            WrittenAt: string.Empty,
            Services: new Dictionary<string, WarmupServiceStatus>());

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

        [JsonPropertyName("planRef")]
        public string? PlanRef { get; set; }

        [JsonPropertyName("routerAlias")]
        public string? RouterAlias { get; set; }

        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }

        [JsonPropertyName("bundleId")]
        public string? BundleId { get; set; }
    }
}
