using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Configuration;

namespace GuideAntsApi.Endpoints;

/// <summary>
/// Resolution of the HTTP admin base URL for each local AI service and
/// validation helpers for the local-models proxy endpoints.
///
/// Operators configure only the host URL (for example
/// <c>LocalServiceHosts:ImageGenerationBaseUrl=http://guideants-ai:80</c>);
/// the per-service admin prefix (<c>/sd</c>, <c>/asr</c>, <c>/tts</c>,
/// <c>/emb</c>) is a
/// product decision and lives here, matching the nginx routing inside the
/// guideants-ai container (see <c>docker/build/guideants-ai/nginx.conf</c>).
/// </summary>
public static class LocalServiceAdminRouting
{
    /// <summary>
    /// Returns the fully-composed admin base URL for the given service (host
    /// + per-service admin prefix), or <c>null</c> if the service either does
    /// not expose local admin endpoints or has no host configured.
    /// </summary>
    public static string? ResolveAdminBase(string serviceId, IConfiguration configuration)
    {
        var host = serviceId switch
        {
            "ImageGeneration" => configuration["LocalServiceHosts:ImageGenerationBaseUrl"],
            "SpeechTranscription" => configuration["LocalServiceHosts:SpeechTranscriptionBaseUrl"],
            "SpeechSynthesis" => configuration["LocalServiceHosts:SpeechSynthesisBaseUrl"],
            "Embeddings" => configuration["LocalServiceHosts:EmbeddingsBaseUrl"],
            _ => null
        };
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }
        if (!RuntimeConfigurationPlaceholders.HasUsableUrl(host))
        {
            return null;
        }
        var prefix = AdminPrefix(serviceId);
        if (prefix is null)
        {
            return null;
        }
        return $"{host.TrimEnd('/')}{prefix}";
    }

    /// <summary>
    /// The admin path prefix that the guideants-ai nginx exposes for each
    /// local service, or <c>null</c> if the service id is not one of the
    /// proxied local services.
    /// </summary>
    public static string? AdminPrefix(string serviceId) =>
        serviceId switch
        {
            "ImageGeneration" => ServiceRoutingContracts.ImageGenerationAdminPath,
            "SpeechTranscription" => ServiceRoutingContracts.SpeechTranscriptionAdminPath,
            "SpeechSynthesis" => ServiceRoutingContracts.SpeechSynthesisAdminPath,
            "Embeddings" => ServiceRoutingContracts.EmbeddingsAdminPath,
            _ => null
        };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="payload"/> is a JSON object
    /// containing a non-empty, non-whitespace string-valued property named
    /// <paramref name="propertyName"/>.
    /// </summary>
    public static bool TryGetNonEmptyString(JsonElement payload, string propertyName, out string value)
    {
        value = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (!payload.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        var raw = prop.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        value = raw.Trim();
        return true;
    }

    /// <summary>
    /// Builds an <see cref="HttpContent"/> that forwards the caller's JSON
    /// body to an upstream local-admin endpoint with the resolved Hugging
    /// Face token stamped into the object under <c>hf_token</c>. When
    /// <paramref name="resolvedHfToken"/> is <c>null</c> the property is
    /// omitted entirely so downstream Pydantic validators can distinguish
    /// "no token configured" from "empty string".
    ///
    /// <para>
    /// The incoming <paramref name="payload"/> is expected to be a JSON
    /// object; any caller-supplied <c>hf_token</c> on the wire is ignored
    /// and overwritten by the server-resolved value so the single source of
    /// truth cannot be bypassed by a crafted request body.
    /// </para>
    /// </summary>
    public static StringContent BuildForwardedBodyWithHfToken(
        JsonElement payload,
        string? resolvedHfToken)
    {
        JsonObject forwarded;
        if (payload.ValueKind == JsonValueKind.Object)
        {
            var node = JsonNode.Parse(payload.GetRawText());
            forwarded = node as JsonObject ?? new JsonObject();
        }
        else
        {
            forwarded = new JsonObject();
        }

        forwarded.Remove("hf_token");
        if (!string.IsNullOrWhiteSpace(resolvedHfToken))
        {
            forwarded["hf_token"] = resolvedHfToken;
        }

        return new StringContent(forwarded.ToJsonString(), Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Status codes that are treated as a "successful proxy" and pass through
    /// the upstream body verbatim with its original status code. Everything
    /// else is wrapped in a <see cref="UpstreamProxyEnvelope"/> and surfaced
    /// as a 502 so the client cannot mistake an upstream 404 for the local
    /// server route being missing.
    /// </summary>
    /// <remarks>
    /// The <c>/ready</c> probes used by ASR/TTS return 503 with a structured
    /// payload when the model is not yet loaded; that is a legitimate answer
    /// from a reachable upstream, so we include 503 here as a pass-through.
    /// </remarks>
    private static readonly HashSet<HttpStatusCode> PassThroughStatusCodes = new()
    {
        HttpStatusCode.OK,
        HttpStatusCode.Created,
        HttpStatusCode.Accepted,
        HttpStatusCode.NoContent,
        HttpStatusCode.BadRequest,       // upstream validation failure with useful body
        HttpStatusCode.Conflict,         // upstream operation conflict with useful body
        HttpStatusCode.ServiceUnavailable, // ready probes return 503 when not loaded
    };

    /// <summary>
    /// Envelope returned when an upstream proxy call fails. The fields are
    /// written intentionally so the UI can render the full proxy chain
    /// verbatim — no field is optional and none are suppressed.
    /// </summary>
    public sealed record UpstreamProxyEnvelope(
        string Error,
        string UpstreamTarget,
        int UpstreamStatus,
        string UpstreamStatusText,
        string UpstreamContentType,
        string UpstreamBody);

    /// <summary>
    /// Proxies <paramref name="request"/> through <paramref name="httpClient"/>
    /// and converts the upstream response into an <see cref="IResult"/>. On a
    /// pass-through status code the body is forwarded with the upstream
    /// status. On any other status code the upstream response is wrapped in
    /// a <see cref="UpstreamProxyEnvelope"/> and returned as <c>502 Bad
    /// Gateway</c> so the UI cannot confuse an upstream 404 with a missing
    /// local route.
    /// </summary>
    public static async Task<IResult> ProxyAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var upstreamTarget = request.RequestUri?.ToString() ?? string.Empty;
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            var envelope = new UpstreamProxyEnvelope(
                Error: $"Upstream request to {upstreamTarget} failed: {ex.Message}",
                UpstreamTarget: upstreamTarget,
                UpstreamStatus: 0,
                UpstreamStatusText: "NetworkError",
                UpstreamContentType: string.Empty,
                UpstreamBody: string.Empty);
            return Results.Json(envelope, statusCode: (int)HttpStatusCode.BadGateway);
        }

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;

            if (PassThroughStatusCodes.Contains(response.StatusCode))
            {
                // Pass through the upstream body verbatim with its status code.
                // We always claim JSON as the response content type — every
                // local admin endpoint we proxy to emits JSON, and the client
                // expects JSON; if the upstream emitted something else we
                // fall back to its declared content type.
                var passThroughContentType =
                    string.IsNullOrWhiteSpace(contentType) ? "application/json" : contentType;
                return Results.Content(body, passThroughContentType, null, (int)response.StatusCode);
            }

            var envelope = new UpstreamProxyEnvelope(
                Error: $"Upstream {upstreamTarget} returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                UpstreamTarget: upstreamTarget,
                UpstreamStatus: (int)response.StatusCode,
                UpstreamStatusText: response.ReasonPhrase ?? string.Empty,
                UpstreamContentType: contentType,
                UpstreamBody: body);
            return Results.Json(envelope, statusCode: (int)HttpStatusCode.BadGateway);
        }
        finally
        {
            response.Dispose();
        }
    }
}
