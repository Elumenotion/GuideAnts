using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GuideAnts.Logging;
using GuideAntsApi.Configuration;
using Microsoft.AspNetCore.Http;

namespace GuideAntsApi.Services.SystemGuide;

public sealed class SystemGuideSandboxAdminProxy(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<SystemGuideSandboxAdminProxy> logger) : ISystemGuideSandboxAdminProxy
{
    private const string AdminTokenHeaderName = "X-Script-Agent-Admin-Token";
    private const string AdminTokenConfigKey = "ScriptExecution:AdminToken";
    private static readonly HashSet<string> AllowedRequestContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "application/json"
    };

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<SystemGuideSandboxAdminProxy> _logger = logger;

    public async Task<IResult> ForwardAsync(
        HttpMethod method,
        string adminPath,
        IReadOnlyDictionary<string, string?>? query,
        string? body,
        string? contentType,
        CancellationToken cancellationToken)
    {
        var baseUrl = RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(
            _configuration[ServiceRoutingContracts.GuideantsAiBaseUrlKey]);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Results.Problem(
                title: "Sandbox admin unavailable",
                detail: $"{ServiceRoutingContracts.GuideantsAiBaseUrlKey} is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var adminToken = RuntimeConfigurationPlaceholders.NormalizeConfiguredValueOrNull(
                _configuration[AdminTokenConfigKey])
            ?? RuntimeConfigurationPlaceholders.NormalizeConfiguredValueOrNull(
                Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_TOKEN"));
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            return Results.Problem(
                title: "Sandbox admin unavailable",
                detail: $"{AdminTokenConfigKey} is not configured on the API host.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var upstreamBase = baseUrl.TrimEnd('/');
        var upstreamPath = adminPath.TrimStart('/');
        var upstreamUrl = $"{upstreamBase}/admin/{upstreamPath}";
        if (query != null && query.Count > 0)
        {
            var queryString = QueryString.Create(
                query
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                    .Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
            upstreamUrl += queryString.ToUriComponent();
        }

        using var request = new HttpRequestMessage(method, upstreamUrl);
        request.Headers.TryAddWithoutValidation(AdminTokenHeaderName, adminToken);
        if (body != null)
        {
            request.Content = CreateUpstreamRequestContent(body, contentType);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Sandbox admin proxy request failed. method={Method} path={Path}",
                method.Method,
                LogValueSanitizer.Sanitize(adminPath));
            return Results.Problem(
                title: "Sandbox admin proxy failure",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }

        try
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent
                || string.IsNullOrWhiteSpace(responseBody))
            {
                return Results.StatusCode((int)response.StatusCode);
            }

            var responseContentType = ResolveSafeResponseContentType(
                response.Content.Headers.ContentType?.ToString());
            if (responseContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(responseBody);
                return Results.Json(document.RootElement.Clone(), statusCode: (int)response.StatusCode);
            }

            return Results.Content(
                responseBody,
                responseContentType,
                Encoding.UTF8,
                (int)response.StatusCode);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static HttpContent CreateUpstreamRequestContent(string body, string? contentType)
    {
        var mediaType = string.IsNullOrWhiteSpace(contentType) ? "application/json" : contentType;
        if (!AllowedRequestContentTypes.Contains(mediaType))
        {
            throw new InvalidOperationException(
                $"Sandbox admin proxy only supports request content types: {string.Join(", ", AllowedRequestContentTypes)}.");
        }

        var content = mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            ? new ByteArrayContent(CanonicalizeJsonUtf8(body))
            : new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType)
        {
            CharSet = "utf-8"
        };
        return content;
    }

    private static string ResolveSafeResponseContentType(string? upstreamContentType)
    {
        if (string.IsNullOrWhiteSpace(upstreamContentType))
        {
            return "application/json";
        }

        if (upstreamContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || upstreamContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return upstreamContentType;
        }

        return "application/json";
    }

    private static byte[] CanonicalizeJsonUtf8(string body)
    {
        using var document = JsonDocument.Parse(body);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document.RootElement));
    }
}
