using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Usage;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.SandboxWireApi;

public interface ISandboxWireExecutionContextResolver
{
    Task<SandboxWireExecutionResolution> ResolveAsync(
        HttpContext httpContext,
        string endpointName,
        int? endpointMaxBytes = null,
        CancellationToken ct = default);
}

public sealed class SandboxWireExecutionContextResolver : ISandboxWireExecutionContextResolver
{
    private static readonly JsonSerializerOptions WireApiJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISandboxWireJwtService _jwtService;
    private readonly ApplicationDbContext _db;
    private readonly ISandboxWireCostLimitService _costLimits;
    private readonly ILogger<SandboxWireExecutionContextResolver> _logger;

    public SandboxWireExecutionContextResolver(
        ISandboxWireJwtService jwtService,
        ApplicationDbContext db,
        ISandboxWireCostLimitService costLimits,
        ILogger<SandboxWireExecutionContextResolver> logger)
    {
        _jwtService = jwtService;
        _db = db;
        _costLimits = costLimits;
        _logger = logger;
    }

    public async Task<SandboxWireExecutionResolution> ResolveAsync(
        HttpContext httpContext,
        string endpointName,
        int? endpointMaxBytes = null,
        CancellationToken ct = default)
    {
        var bearerToken = ReadBearerToken(httpContext);
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return SandboxWireExecutionResolution.Fail(
                OpenAiWireErrorResults.AuthenticationFailed(
                    "Missing bearer token.",
                    code: "invalid_sandbox_wire_token"));
        }

        if (!_jwtService.TryValidate(bearerToken, out var grant, out var failureReason))
        {
            return SandboxWireExecutionResolution.Fail(
                OpenAiWireErrorResults.AuthenticationFailed(
                    failureReason ?? "Invalid sandbox wire token.",
                    code: "invalid_sandbox_wire_token"));
        }

        if (!grant!.AllowedEndpoints.Contains(endpointName, StringComparer.Ordinal))
        {
            return SandboxWireExecutionResolution.Fail(OpenAiWireErrorResults.EndpointDisabled(endpointName));
        }

        var ownerGuide = await _db.Assistants
            .AsNoTracking()
            .Where(a => a.Id == grant.OwnerAssistantId && a.Kind == DataModel.Models.AssistantKind.Guide)
            .Select(a => new { a.Id, a.SandboxWireApiConfigJson })
            .FirstOrDefaultAsync(ct);

        if (ownerGuide == null)
        {
            return SandboxWireExecutionResolution.Fail(
                OpenAiWireErrorResults.Create(
                    StatusCodes.Status404NotFound,
                    "Owner guide not found.",
                    type: "invalid_request_error",
                    code: "invalid_owner_guide"));
        }

        SandboxWireApiConfigDto wireConfig;
        try
        {
            wireConfig = DeserializeWireApiConfig(ownerGuide.SandboxWireApiConfigJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid sandbox wire config for guide {GuideId}", grant.OwnerAssistantId);
            return SandboxWireExecutionResolution.Fail(
                OpenAiWireErrorResults.ProviderNotReady("Sandbox wire API configuration is invalid."));
        }

        if (!IsEndpointEnabled(wireConfig, endpointName))
        {
            return SandboxWireExecutionResolution.Fail(OpenAiWireErrorResults.EndpointDisabled(endpointName));
        }

        var effectiveMaxBytes = endpointMaxBytes ?? ResolveConfiguredMaxBytes(wireConfig, endpointName);
        var requestBytes = httpContext.Request.ContentLength;
        if (effectiveMaxBytes.HasValue && effectiveMaxBytes.Value > 0 && requestBytes.HasValue && requestBytes.Value > effectiveMaxBytes.Value)
        {
            return SandboxWireExecutionResolution.Fail(OpenAiWireErrorResults.RequestTooLarge(endpointName, effectiveMaxBytes));
        }

        var notebookExists = await _db.Notebooks
            .AsNoTracking()
            .AnyAsync(n => n.Id == grant.NotebookId && n.ProjectId == grant.ProjectId, ct);
        if (!notebookExists)
        {
            return SandboxWireExecutionResolution.Fail(
                OpenAiWireErrorResults.Create(
                    StatusCodes.Status404NotFound,
                    "Notebook not found for sandbox wire execution.",
                    type: "invalid_request_error",
                    code: "invalid_notebook"));
        }

        var limitResult = await _costLimits.EnsureWithinLimitsAsync(
            new SandboxWireCostLimitScope(
                grant.OwnerAssistantId,
                grant.DailyLimitUsd ?? wireConfig.DailyLimitUsd,
                grant.MonthlyLimitUsd ?? wireConfig.MonthlyLimitUsd),
            grant.NotebookId,
            ct);
        if (!limitResult.Allowed)
        {
            return SandboxWireExecutionResolution.Fail(OpenAiWireErrorResults.LimitExceeded(limitResult));
        }

        var context = new SandboxWireExecutionContext
        {
            ProjectId = grant.ProjectId,
            NotebookId = grant.NotebookId,
            OwnerAssistantId = grant.OwnerAssistantId,
            TargetAssistantId = grant.TargetAssistantId,
            TargetAssistantName = grant.TargetAssistantName,
            AttributionConversationId = grant.AttributionConversationId,
            SourceChannel = SandboxWireExecutionContext.SourceChannelValue,
            ExternalRequestId = UsageAttributionHttpContext.ResolveExternalRequestId(httpContext),
            EndpointName = endpointName,
            EndpointFlags = wireConfig.EndpointFlags,
            AliasMap = wireConfig.AliasMap,
            MaxRequestSizes = wireConfig.MaxRequestSizes,
            AllowedEndpoints = grant.AllowedEndpoints,
        };

        httpContext.Items[WireExecutionContextHttpContextExtensions.HttpContextItemKey] = context;
        UsageAttributionHttpContext.Set(
            httpContext,
            new UsageAttributionContext(
                PublishedGuideId: null,
                SourceChannel: context.SourceChannel,
                ExternalRequestId: context.ExternalRequestId,
                ExternalUserIdentity: $"sandbox:{grant.ExecutionId:N}"));

        return SandboxWireExecutionResolution.Pass(context);
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

    private static SandboxWireApiConfigDto DeserializeWireApiConfig(string? wireApiConfigJson)
    {
        if (string.IsNullOrWhiteSpace(wireApiConfigJson))
        {
            return new SandboxWireApiConfigDto();
        }

        return JsonSerializer.Deserialize<SandboxWireApiConfigDto>(wireApiConfigJson, WireApiJsonOptions)
            ?? new SandboxWireApiConfigDto();
    }

    private static bool IsEndpointEnabled(SandboxWireApiConfigDto wireApiConfig, string endpointName)
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
            "messages" => flags.Messages != false,
            "embeddings" => flags.Embeddings != false,
            "images.generations" => flags.ImageGenerations != false,
            "audio.transcriptions" => flags.AudioTranscriptions != false,
            "audio.speech" => flags.AudioSpeech != false,
            _ => true
        };
    }

    private static int? ResolveConfiguredMaxBytes(SandboxWireApiConfigDto wireApiConfig, string endpointName)
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
            "messages" => max.MessagesBytes,
            "embeddings" => max.EmbeddingsBytes,
            "images.generations" => max.ImageGenerationsBytes,
            "audio.transcriptions" => max.AudioTranscriptionsBytes,
            "audio.speech" => max.AudioSpeechBytes,
            _ => null
        };
    }
}
