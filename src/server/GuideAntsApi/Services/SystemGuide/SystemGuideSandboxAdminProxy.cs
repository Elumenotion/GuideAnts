using System.Net.Http;
using System.Text;
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
            request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
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
                adminPath);
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

            var responseContentType = response.Content.Headers.ContentType?.ToString();
            return Results.Content(
                responseBody,
                string.IsNullOrWhiteSpace(responseContentType) ? "application/json" : responseContentType,
                Encoding.UTF8,
                (int)response.StatusCode);
        }
        finally
        {
            response.Dispose();
        }
    }
}
