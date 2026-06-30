using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GuideAntsApi.Configuration;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Typed ScriptExecutionAgent admin client for scoped guide sandbox operations.
/// </summary>
public sealed class McpSandboxAdminApiClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<McpSandboxAdminApiClient> logger)
{
    private const string AdminTokenHeaderName = "X-Script-Agent-Admin-Token";
    private const string AdminTokenConfigKey = "ScriptExecution:AdminToken";

    public async Task<string?> GetTextAsync(
        string adminPath,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, adminPath, query, body: null, contentType: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body;
    }

    public async Task<bool> PutTextAsync(
        string adminPath,
        IReadOnlyDictionary<string, string?>? query,
        string body,
        string contentType,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Put, adminPath, query, body, contentType, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<JsonDocument?> GetJsonAsync(
        string adminPath,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, adminPath, query, body: null, contentType: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return JsonDocument.Parse(body);
    }

    public async Task<bool> PostNoContentAsync(
        string adminPath,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, adminPath, query, body: null, contentType: null, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Accepted;
    }

    public static IReadOnlyDictionary<string, string?> BuildScopedQuery(Guid projectId, Guid guideId) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["projectId"] = projectId.ToString("D"),
            ["guideId"] = guideId.ToString("D"),
        };

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string adminPath,
        IReadOnlyDictionary<string, string?>? query,
        string? body,
        string? contentType,
        CancellationToken cancellationToken)
    {
        var baseUrl = RuntimeConfigurationPlaceholders.NormalizeUrlOrNull(
            configuration[ServiceRoutingContracts.GuideantsAiBaseUrlKey]);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException($"{ServiceRoutingContracts.GuideantsAiBaseUrlKey} is not configured.");
        }

        var adminToken = RuntimeConfigurationPlaceholders.NormalizeConfiguredValueOrNull(
                configuration[AdminTokenConfigKey])
            ?? RuntimeConfigurationPlaceholders.NormalizeConfiguredValueOrNull(
                Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_TOKEN"));
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            throw new InvalidOperationException($"{AdminTokenConfigKey} is not configured on the API host.");
        }

        var upstreamBase = baseUrl.TrimEnd('/');
        var upstreamPath = adminPath.TrimStart('/');
        var upstreamUrl = $"{upstreamBase}/admin/{upstreamPath}";
        if (query is { Count: > 0 })
        {
            var queryString = Microsoft.AspNetCore.Http.QueryString.Create(
                query.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                    .Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
            upstreamUrl += queryString.ToUriComponent();
        }

        using var request = new HttpRequestMessage(method, upstreamUrl);
        request.Headers.TryAddWithoutValidation(AdminTokenHeaderName, adminToken);
        if (body is not null)
        {
            var mediaType = string.IsNullOrWhiteSpace(contentType) ? "text/plain" : contentType;
            request.Content = new StringContent(body, Encoding.UTF8, mediaType);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType)
            {
                CharSet = "utf-8",
            };
        }

        try
        {
            return await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Sandbox admin client request failed. method={Method} path={Path}", method.Method, adminPath);
            throw;
        }
    }
}
