using GuideAntsApi.Services.Bootstrap;
using System.Diagnostics;
using System.Text.Json;

namespace GuideAntsApi.Services.Reachability;

/// <summary>
/// One presence probe of a local endpoint's readiness/health URL.
///
/// Availability follows the machine-checkable classification rule:
/// <list type="bullet">
/// <item><b>Unreachable</b> = socket-level only (connection refused, DNS
/// failure, connect/response timeout).</item>
/// <item><b>Reachable</b> = any HTTP response, including 503.</item>
/// <item>Loaded / not-loaded / failed comes from the <b>body</b>, never the
/// status code.</item>
/// </list>
///
/// Reading a non-2xx status as "down" would render an up-but-idle engine
/// (which answers 503 while not loaded) as "endpoint unreachable" — a confident
/// false statement, worse than hiding. The body's loaded state is parsed
/// best-effort and surfaced as <see cref="EndpointObservation.RuntimeState"/>.
/// </summary>
public sealed class LocalEndpointProbe : ILocalEndpointProbe
{
    /// <summary>
    /// Per-endpoint probe budget. Mirrors InfrastructureProbeService.ProbeTimeout
    /// (3 s): a tight bound keeps the worst case inside a single
    /// human-perceptible beat. This budget is probe-only; the load path keeps its
    /// full verification budget (R3).
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<DateTime> _nowUtc;

    public LocalEndpointProbe(IHttpClientFactory httpClientFactory, Func<DateTime>? nowUtc = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    public async Task<EndpointObservation> ProbeAsync(string readinessUrl, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            // Reuse the LocalServiceAdmin client name so base-address/timeout
            // conventions match the rest of the local admin surface.
            var client = _httpClientFactory.CreateClient(LocalServiceLoadService.HttpClientName);
            using var response = await client
                .GetAsync(readinessUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            // Any HTTP response is Reachable. Parse the body best-effort for a
            // runtime state (loaded/loading/unloaded) to show (F-2: shown, not hidden).
            string? runtimeState = null;
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                runtimeState = ParseRuntimeState(body);
            }
            catch
            {
                // Body parsing is best-effort; reachability is already settled.
            }

            stopwatch.Stop();
            return new EndpointObservation(
                readinessUrl,
                EndpointAvailability.Reachable,
                _nowUtc(),
                (int)stopwatch.ElapsedMilliseconds,
                Error: null,
                ConsecutiveFailures: 0,
                RuntimeState: runtimeState);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Socket-level failure (refused / DNS / timeout): the only
            // Unreachable condition.
            stopwatch.Stop();
            return new EndpointObservation(
                readinessUrl,
                EndpointAvailability.Unreachable,
                _nowUtc(),
                (int)stopwatch.ElapsedMilliseconds,
                Error: ex.Message,
                ConsecutiveFailures: 1);
        }
    }

    /// <summary>
    /// Best-effort parse of a readiness/health body into a displayable runtime
    /// state. ASR/TTS/Embeddings <c>/ready</c> emit
    /// <c>{"loaded":bool,"loading":bool,...}</c>; the SD wrapper <c>/health</c>
    /// emits <c>{"status":"unloaded"|"ok"|"degraded",...}</c>. Unknown shapes
    /// yield null (rendered as unobserved), never a thrown error (P6).
    /// </summary>
    internal static string? ParseRuntimeState(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            static bool GetBool(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            static string? GetString(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var loaded = GetBool(root, "loaded");
            var loading = GetBool(root, "loading");
            var status = GetString(root, "status");

            if (loaded || string.Equals(status, "ok", StringComparison.Ordinal))
            {
                return "loaded";
            }
            if (loading)
            {
                return "loading";
            }
            if (status is not null)
            {
                return status; // "unloaded", "degraded", ...
            }
            return "unloaded";
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
