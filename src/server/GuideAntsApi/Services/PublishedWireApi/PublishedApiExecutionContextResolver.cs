using System.Security.Claims;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.PublishedGuides;
using GuideAntsApi.Services.Usage;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.PublishedWireApi;

public interface IPublishedApiExecutionContextResolver
{
    Task<PublishedApiExecutionResolution> ResolveAsync(
        HttpContext httpContext,
        Guid pubId,
        string endpointName,
        int? endpointMaxBytes = null,
        CancellationToken ct = default);
}

public sealed class PublishedApiExecutionContextResolver : IPublishedApiExecutionContextResolver
{
    public const string HttpContextItemKey = "PublishedApiExecutionContext";
    public const string WireApiSourceChannel = "wire_api";

    private static readonly JsonSerializerOptions WireApiJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApplicationDbContext _db;
    private readonly IPublishedGuideAuthService _authService;
    private readonly IPublishedGuideCostLimitService _costLimits;
    private readonly ILogger<PublishedApiExecutionContextResolver> _logger;

    public PublishedApiExecutionContextResolver(
        ApplicationDbContext db,
        IPublishedGuideAuthService authService,
        IPublishedGuideCostLimitService costLimits,
        ILogger<PublishedApiExecutionContextResolver> logger)
    {
        _db = db;
        _authService = authService;
        _costLimits = costLimits;
        _logger = logger;
    }

    public async Task<PublishedApiExecutionResolution> ResolveAsync(
        HttpContext httpContext,
        Guid pubId,
        string endpointName,
        int? endpointMaxBytes = null,
        CancellationToken ct = default)
    {
        var publishedGuide = await _db.PublishedGuides
            .AsNoTracking()
            .Include(pg => pg.Notebook)
            .FirstOrDefaultAsync(pg => pg.Id == pubId && pg.Active, ct);

        if (publishedGuide == null)
        {
            return PublishedApiExecutionResolution.Fail(
                OpenAiWireErrorResults.Create(
                    StatusCodes.Status404NotFound,
                    "Published guide not found or inactive.",
                    type: "invalid_request_error",
                    code: "invalid_published_guide"));
        }

        PublishedWireApiConfigDto wireApiConfig;
        try
        {
            wireApiConfig = DeserializeWireApiConfig(publishedGuide.WireApiConfigJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Invalid wire API config JSON for published guide {PubId}",
                LogValueSanitizer.Sanitize(pubId));
            return PublishedApiExecutionResolution.Fail(
                OpenAiWireErrorResults.ProviderNotReady("Published guide wire API configuration is invalid."));
        }

        if (!wireApiConfig.Enabled)
        {
            return PublishedApiExecutionResolution.Fail(OpenAiWireErrorResults.EndpointDisabled(endpointName));
        }

        if (!IsEndpointEnabled(wireApiConfig, endpointName))
        {
            return PublishedApiExecutionResolution.Fail(OpenAiWireErrorResults.EndpointDisabled(endpointName));
        }

        var effectiveMaxBytes = endpointMaxBytes ?? ResolveConfiguredMaxBytes(wireApiConfig, endpointName);
        var requestBytes = httpContext.Request.ContentLength;
        if (effectiveMaxBytes.HasValue && effectiveMaxBytes.Value > 0 && requestBytes.HasValue && requestBytes.Value > effectiveMaxBytes.Value)
        {
            return PublishedApiExecutionResolution.Fail(OpenAiWireErrorResults.RequestTooLarge(endpointName, effectiveMaxBytes));
        }

        var authMode = ResolveAuthMode(publishedGuide, httpContext.User);
        var externalUserIdentity = ResolveAuthenticatedUserIdentity(httpContext.User);

        if (authMode != PublishedApiAuthMode.AppIdentity)
        {
            var authResult = await ValidateAuthAsync(
                httpContext,
                pubId,
                publishedGuide.Notebook.ProjectId,
                publishedGuide.NotebookId,
                authMode,
                ct);

            if (!authResult.IsValid)
            {
                var errorCode = authResult.ErrorCode ?? "authentication_failed";
                return PublishedApiExecutionResolution.Fail(
                    OpenAiWireErrorResults.AuthenticationFailed(
                        authResult.ErrorMessage ?? "Authentication failed.",
                        code: errorCode));
            }

            if (!string.IsNullOrWhiteSpace(authResult.UserIdentity))
            {
                externalUserIdentity = authResult.UserIdentity;
            }
        }

        var limitResult = await _costLimits.EnsureWithinLimitsAsync(publishedGuide.NotebookId, ct);
        if (!limitResult.Allowed)
        {
            return PublishedApiExecutionResolution.Fail(OpenAiWireErrorResults.LimitExceeded(limitResult));
        }

        var executionContext = new PublishedApiExecutionContext(
            PubId: pubId,
            ProjectId: publishedGuide.Notebook.ProjectId,
            NotebookId: publishedGuide.NotebookId,
            GuideId: publishedGuide.GuideId,
            PublishedGuide: publishedGuide,
            WireApiConfig: wireApiConfig,
            AuthMode: authMode,
            ExternalUserIdentity: externalUserIdentity,
            SourceChannel: WireApiSourceChannel,
            ExternalRequestId: ResolveExternalRequestId(httpContext),
            EndpointName: endpointName);

        httpContext.Items[HttpContextItemKey] = executionContext;
        UsageAttributionHttpContext.Set(
            httpContext,
            new UsageAttributionContext(
                PublishedGuideId: pubId,
                SourceChannel: WireApiSourceChannel,
                ExternalRequestId: executionContext.ExternalRequestId,
                ExternalUserIdentity: executionContext.ExternalUserIdentity));
        return PublishedApiExecutionResolution.Pass(executionContext);
    }

    private async Task<AuthValidationResult> ValidateAuthAsync(
        HttpContext httpContext,
        Guid pubId,
        Guid projectId,
        Guid notebookId,
        PublishedApiAuthMode authMode,
        CancellationToken ct)
    {
        var bearerToken = ReadBearerToken(httpContext);
        var apiKeyHeader = httpContext.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
        var webhookHeader = httpContext.Request.Headers["X-Published-Auth"].ToString();

        return authMode switch
        {
            PublishedApiAuthMode.ApiKey => await _authService.ValidateAsync(
                pubId,
                authorizationHeader: null,
                projectId: projectId,
                notebookId: notebookId,
                ct: ct,
                apiKeyHeader: string.IsNullOrWhiteSpace(apiKeyHeader) ? bearerToken : apiKeyHeader),
            PublishedApiAuthMode.Webhook => await _authService.ValidateAsync(
                pubId,
                authorizationHeader: string.IsNullOrWhiteSpace(webhookHeader) ? bearerToken : webhookHeader,
                projectId: projectId,
                notebookId: notebookId,
                ct: ct,
                apiKeyHeader: null),
            _ => new AuthValidationResult { IsValid = true }
        };
    }

    private static PublishedApiAuthMode ResolveAuthMode(
        DataModel.Models.PublishedGuide publishedGuide,
        ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated == true)
        {
            return PublishedApiAuthMode.AppIdentity;
        }

        if (!string.IsNullOrWhiteSpace(publishedGuide.ApiKeyHash))
        {
            return PublishedApiAuthMode.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(publishedGuide.AuthValidationWebhookUrl))
        {
            return PublishedApiAuthMode.Webhook;
        }

        return PublishedApiAuthMode.Anonymous;
    }

    private static string? ReadBearerToken(HttpContext httpContext)
    {
        var authorization = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return null;
        }

        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[bearerPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string ResolveExternalRequestId(HttpContext httpContext)
        => UsageAttributionHttpContext.ResolveExternalRequestId(httpContext);

    private static string? ResolveAuthenticatedUserIdentity(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.Identity?.Name;
    }

    private static PublishedWireApiConfigDto DeserializeWireApiConfig(string? wireApiConfigJson)
    {
        if (string.IsNullOrWhiteSpace(wireApiConfigJson))
        {
            return new PublishedWireApiConfigDto();
        }

        return JsonSerializer.Deserialize<PublishedWireApiConfigDto>(wireApiConfigJson, WireApiJsonOptions)
            ?? new PublishedWireApiConfigDto();
    }

    private static bool IsEndpointEnabled(PublishedWireApiConfigDto wireApiConfig, string endpointName)
    {
        var flags = wireApiConfig.EndpointFlags;
        if (flags == null)
        {
            return true;
        }

        return endpointName switch
        {
            "models" => flags.Models != false,
            "chat.completions" => flags.ChatCompletions != false,
            "responses" => flags.Responses != false,
            "embeddings" => flags.Embeddings != false,
            "images.generations" => flags.ImageGenerations != false,
            "audio.transcriptions" => flags.AudioTranscriptions != false,
            "audio.speech" => flags.AudioSpeech != false,
            _ => true
        };
    }

    private static int? ResolveConfiguredMaxBytes(PublishedWireApiConfigDto wireApiConfig, string endpointName)
    {
        var max = wireApiConfig.MaxRequestSizes;
        if (max == null)
        {
            return null;
        }

        return endpointName switch
        {
            "chat.completions" => max.ChatCompletionsBytes,
            "responses" => max.ResponsesBytes,
            "embeddings" => max.EmbeddingsBytes,
            "images.generations" => max.ImageGenerationsBytes,
            "audio.transcriptions" => max.AudioTranscriptionsBytes,
            "audio.speech" => max.AudioSpeechBytes,
            _ => null
        };
    }
}
