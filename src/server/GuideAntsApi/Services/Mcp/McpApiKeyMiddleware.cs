using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.PublishedGuides;
using GuideAntsApi.Services.Usage;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Middleware that authenticates MCP requests on the <c>/api/published/mcp</c> path.
/// Validates the <c>pubId</c> query parameter and the <c>x-guideants-apikey</c> header,
/// checks that the published guide is active, MCP-enabled, and within cost limits,
/// then populates <see cref="McpPublishedGuideContext"/> for downstream MCP tool methods.
/// </summary>
public class McpApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<McpApiKeyMiddleware> _logger;

    public McpApiKeyMiddleware(RequestDelegate next, ILogger<McpApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var pubIdStr = context.Request.Query["pubId"].ToString();
        if (string.IsNullOrWhiteSpace(pubIdStr) || !Guid.TryParse(pubIdStr, out var pubId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "missing_pub_id", message = "The 'pubId' query parameter is required." });
            return;
        }

        var apiKey = context.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "api_key_required", message = "MCP access requires an API key via the x-guideants-apikey header." });
            return;
        }

        var db = context.RequestServices.GetRequiredService<ApplicationDbContext>();

        var publishedGuide = await db.PublishedGuides
            .AsNoTracking()
            .Include(pg => pg.Notebook)
            .Include(pg => pg.Guide)
                .ThenInclude(g => g!.CrewMembers)
                    .ThenInclude(cm => cm.Assistant)
            .FirstOrDefaultAsync(pg => pg.Id == pubId && pg.Active);

        if (publishedGuide == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "not_found", message = "Published guide not found or inactive." });
            return;
        }

        if (!publishedGuide.McpEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "mcp_not_enabled", message = "MCP is not enabled for this published guide." });
            return;
        }

        if (string.IsNullOrWhiteSpace(publishedGuide.ApiKeyHash))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "mcp_requires_api_key", message = "MCP requires an API key to be configured on the published guide." });
            return;
        }

        var providedHash = PublishedGuideAuthService.HashApiKey(apiKey);
        if (!string.Equals(providedHash, publishedGuide.ApiKeyHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Invalid MCP API key for published guide {PubId}", pubId);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_api_key", message = "The provided API key is invalid." });
            return;
        }

        var costLimits = context.RequestServices.GetRequiredService<IPublishedGuideCostLimitService>();
        var limitResult = await costLimits.EnsureWithinLimitsAsync(publishedGuide.NotebookId, context.RequestAborted);
        if (!limitResult.Allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "published_guide_cost_limit_exceeded",
                reason = limitResult.Reason,
                dailyLimitUsd = limitResult.DailyLimitUsd,
                dailyChargeUsd = limitResult.DailyChargeUsd,
                billingPeriodLimitUsd = limitResult.BillingPeriodLimitUsd,
                billingPeriodChargeUsd = limitResult.BillingPeriodChargeUsd
            });
            return;
        }

        var mcpContext = context.RequestServices.GetRequiredService<McpPublishedGuideContext>();
        mcpContext.PubId = pubId;
        mcpContext.ProjectId = publishedGuide.Notebook.ProjectId;
        mcpContext.NotebookId = publishedGuide.NotebookId;
        mcpContext.GuideId = publishedGuide.GuideId;
        mcpContext.UserIdentity = "api-key-user";
        mcpContext.IsValid = true;
        mcpContext.GuideName = publishedGuide.Guide?.Name ?? string.Empty;
        mcpContext.GuideDescription = publishedGuide.Guide?.Description;
        mcpContext.McpDescription = publishedGuide.McpDescription;
        mcpContext.PublicApiOrigin = ResolvePublicApiOrigin(context);
        mcpContext.AddressableAssistants = await McpPublishedAssistantCatalog.LoadAsync(
            db,
            publishedGuide.GuideId,
            publishedGuide.McpDescription,
            context.RequestAborted);

        UsageAttributionHttpContext.Set(
            context,
            new UsageAttributionContext(
                PublishedGuideId: pubId,
                SourceChannel: "mcp",
                ExternalRequestId: UsageAttributionHttpContext.ResolveExternalRequestId(context),
                ExternalUserIdentity: mcpContext.UserIdentity));

        await _next(context);
    }

    private static string ResolvePublicApiOrigin(HttpContext context)
    {
        var scheme = context.Request.Scheme?.Trim();
        if (string.IsNullOrWhiteSpace(scheme))
        {
            throw new InvalidOperationException("Unable to resolve MCP public API origin because request scheme is missing.");
        }

        if (!context.Request.Host.HasValue)
        {
            throw new InvalidOperationException("Unable to resolve MCP public API origin because request host is missing.");
        }

        return $"{scheme}://{context.Request.Host.Value}".TrimEnd('/');
    }
}
