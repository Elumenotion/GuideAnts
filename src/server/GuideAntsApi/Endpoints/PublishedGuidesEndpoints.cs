using AntRunner.Chat;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.PublishedGuides;

namespace GuideAntsApi.Endpoints;

public static class PublishedGuidesEndpoints
{
    private static readonly JsonSerializerOptions UsageJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapPublishedGuidesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/published/guides")
            .WithTags("PublishedGuides")
            .AllowAnonymous()
            .RequireCors("PublicApiCors");

        // Get PublishedGuide configuration by ID
        group.MapGet("/{pubId:guid}", async (
            [FromServices] ApplicationDbContext db,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            Guid pubId) =>
        {
            var publishedGuide = await db.PublishedGuides
                .AsNoTracking()
                .Include(pg => pg.Guide)
                    .ThenInclude(g => g.ConversationStarters)
                .Include(pg => pg.Notebook)
                    .ThenInclude(n => n.Project)
                .Where(pg => pg.Id == pubId && pg.Active)
                .FirstOrDefaultAsync();

            if (publishedGuide == null)
            {
                return Results.NotFound(new { error = "Published guide not found or inactive" });
            }

            var limitResult = await costLimits.EnsureWithinLimitsAsync(publishedGuide.NotebookId);
            if (!limitResult.Allowed)
            {
                return Results.Json(new
                {
                    error = "published_guide_cost_limit_exceeded",
                    reason = limitResult.Reason,
                    dailyLimitUsd = limitResult.DailyLimitUsd,
                    dailyChargeUsd = limitResult.DailyChargeUsd,
                    dailyWindowStartUtc = limitResult.DailyWindowStartUtc,
                    dailyWindowEndUtc = limitResult.DailyWindowEndUtc,
                    billingPeriodLimitUsd = limitResult.BillingPeriodLimitUsd,
                    billingPeriodChargeUsd = limitResult.BillingPeriodChargeUsd,
                    billingPeriodStartUtc = limitResult.BillingPeriodStartUtc,
                    billingPeriodEndUtc = limitResult.BillingPeriodEndUtc
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            var requiresApiKey = publishedGuide.AuthMode == PublishedGuideAuthMode.ApiKey
                || !string.IsNullOrWhiteSpace(publishedGuide.ApiKeyHash);

            var response = new
            {
                id = publishedGuide.Id,
                projectId = publishedGuide.Notebook.ProjectId,
                notebookId = publishedGuide.NotebookId,
                guideId = publishedGuide.GuideId,
                guideName = publishedGuide.Guide.Name,
                maxUserMessageLength = publishedGuide.MaxUserMessageLength,
                maxTurns = publishedGuide.MaxTurns,
                dailyChargeLimitUsd = publishedGuide.DailyChargeLimitUsd,
                billingPeriodChargeLimitUsd = publishedGuide.BillingPeriodChargeLimitUsd,
                authMode = publishedGuide.AuthMode.ToString(),
                requiresAuth = publishedGuide.AuthMode != PublishedGuideAuthMode.Anonymous,
                requiresApiKey = requiresApiKey,
                displayMode = publishedGuide.DisplayMode,
                commandMode = publishedGuide.CommandMode,
                showTurnNavigation = publishedGuide.ShowTurnNavigation,
                collapsible = publishedGuide.Collapsible,
                showConversationStarters = publishedGuide.ShowConversationStarters,
                showAttachments = publishedGuide.ShowAttachments,
                mcpEnabled = publishedGuide.McpEnabled,
                mcpEndpoint = publishedGuide.McpEnabled
                    ? $"/api/published/mcp?pubId={publishedGuide.Id}"
                    : null,
                conversationStarters = publishedGuide.Guide.ConversationStarters
                    .OrderBy(cs => cs.OrderIndex)
                    .Select(cs => cs.Prompt)
                    .ToList()
            };

            return Results.Ok(response);
        })
        .Produces<object>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // Get PublishedGuide by friendly name (public, anonymous access)
        group.MapGet("/by-name/{friendlyName}", async (
            [FromServices] ApplicationDbContext db,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            string friendlyName) =>
        {
            // Case-insensitive lookup
            var publishedGuide = await db.PublishedGuides
                .AsNoTracking()
                .Include(pg => pg.Guide)
                    .ThenInclude(g => g.ConversationStarters)
                .Include(pg => pg.Notebook)
                    .ThenInclude(n => n.Project)
                .Where(pg => pg.FriendlyName != null && 
                             pg.FriendlyName.ToLower() == friendlyName.ToLower() && 
                             pg.Active)
                .FirstOrDefaultAsync();

            if (publishedGuide == null)
            {
                return Results.NotFound(new { error = "Published guide not found or inactive" });
            }

            var limitResult = await costLimits.EnsureWithinLimitsAsync(publishedGuide.NotebookId);
            if (!limitResult.Allowed)
            {
                return Results.Json(new
                {
                    error = "published_guide_cost_limit_exceeded",
                    reason = limitResult.Reason,
                    dailyLimitUsd = limitResult.DailyLimitUsd,
                    dailyChargeUsd = limitResult.DailyChargeUsd,
                    dailyWindowStartUtc = limitResult.DailyWindowStartUtc,
                    dailyWindowEndUtc = limitResult.DailyWindowEndUtc,
                    billingPeriodLimitUsd = limitResult.BillingPeriodLimitUsd,
                    billingPeriodChargeUsd = limitResult.BillingPeriodChargeUsd,
                    billingPeriodStartUtc = limitResult.BillingPeriodStartUtc,
                    billingPeriodEndUtc = limitResult.BillingPeriodEndUtc
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            // Build avatar URL if available
            string? avatarUrl = null;
            if (publishedGuide.Guide.AvatarImageBytes != null)
            {
                avatarUrl = $"/api/published/guides/{publishedGuide.Id}/avatar";
            }

            var requiresApiKey = publishedGuide.AuthMode == PublishedGuideAuthMode.ApiKey
                || !string.IsNullOrWhiteSpace(publishedGuide.ApiKeyHash);

            var response = new
            {
                id = publishedGuide.Id,
                friendlyName = publishedGuide.FriendlyName,
                projectId = publishedGuide.Notebook.ProjectId,
                notebookId = publishedGuide.NotebookId,
                guideId = publishedGuide.GuideId,
                guideName = publishedGuide.Guide.Name,
                guideDescription = publishedGuide.Guide.Description,
                avatarUrl = avatarUrl,
                maxUserMessageLength = publishedGuide.MaxUserMessageLength,
                maxTurns = publishedGuide.MaxTurns,
                dailyChargeLimitUsd = publishedGuide.DailyChargeLimitUsd,
                billingPeriodChargeLimitUsd = publishedGuide.BillingPeriodChargeLimitUsd,
                authMode = publishedGuide.AuthMode.ToString(),
                requiresAuth = publishedGuide.AuthMode != PublishedGuideAuthMode.Anonymous,
                requiresApiKey = requiresApiKey,
                displayMode = publishedGuide.DisplayMode,
                commandMode = publishedGuide.CommandMode,
                showTurnNavigation = publishedGuide.ShowTurnNavigation,
                collapsible = publishedGuide.Collapsible,
                showConversationStarters = publishedGuide.ShowConversationStarters,
                showAttachments = publishedGuide.ShowAttachments,
                mcpEnabled = publishedGuide.McpEnabled,
                mcpEndpoint = publishedGuide.McpEnabled
                    ? $"/api/published/mcp?pubId={publishedGuide.Id}"
                    : null,
                conversationStarters = publishedGuide.Guide.ConversationStarters
                    .OrderBy(cs => cs.OrderIndex)
                    .Select(cs => cs.Prompt)
                    .ToList()
            };

            return Results.Ok(response);
        })
        .Produces<object>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // Invoke published guide (one-shot, returns final assistant message)
        group.MapPost("/{pubId:guid}/invoke", async (
            HttpContext ctx,
            [FromServices] ApplicationDbContext db,
            [FromServices] IPublishedGuideAuthService authService,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            [FromServices] IPublishedConversationService conversations,
            Guid pubId,
            [FromBody] PublishedGuideInvokeRequest request) =>
        {
            if (request == null)
            {
                return Results.BadRequest(new { error = "Missing request body" });
            }

            if (string.IsNullOrWhiteSpace(request.Instructions)
                && (request.Attachments == null || request.Attachments.Count == 0))
            {
                return Results.BadRequest(new { error = "Instructions required" });
            }

            var publishedGuide = await db.PublishedGuides
                .AsNoTracking()
                .Include(pg => pg.Notebook)
                    .ThenInclude(n => n.Project)
                .Where(pg => pg.Id == pubId && pg.Active)
                .FirstOrDefaultAsync();

            if (publishedGuide == null)
            {
                return Results.NotFound(new { error = "Published guide not found or inactive" });
            }

            var authHeader = ctx.Request.Headers["X-Published-Auth"].ToString();
            var apiKeyHeader = ctx.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
            var authResult = await authService.ValidateAsync(
                pubId,
                authHeader,
                publishedGuide.Notebook.ProjectId,
                publishedGuide.NotebookId,
                ctx.RequestAborted,
                apiKeyHeader,
                PublishedGuideAuthService.ReadAppAuthCookie(ctx.Request));

            if (!authResult.IsValid)
            {
                var statusCode = PublishedGuideAuthService.MapValidationFailureStatusCode(authResult.ErrorCode);

                return Results.Json(
                    new { error = authResult.ErrorCode, message = authResult.ErrorMessage, requiresAuth = true },
                    statusCode: statusCode);
            }

            var limitResult = await costLimits.EnsureWithinLimitsAsync(
                publishedGuide.NotebookId,
                ctx.RequestAborted);
            if (!limitResult.Allowed)
            {
                return Results.Json(new
                {
                    error = "published_guide_cost_limit_exceeded",
                    reason = limitResult.Reason,
                    dailyLimitUsd = limitResult.DailyLimitUsd,
                    dailyChargeUsd = limitResult.DailyChargeUsd,
                    dailyWindowStartUtc = limitResult.DailyWindowStartUtc,
                    dailyWindowEndUtc = limitResult.DailyWindowEndUtc,
                    billingPeriodLimitUsd = limitResult.BillingPeriodLimitUsd,
                    billingPeriodChargeUsd = limitResult.BillingPeriodChargeUsd,
                    billingPeriodStartUtc = limitResult.BillingPeriodStartUtc,
                    billingPeriodEndUtc = limitResult.BillingPeriodEndUtc
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            var title = string.IsNullOrWhiteSpace(request.Title) ? DateTime.UtcNow.ToString("f", System.Globalization.CultureInfo.InvariantCulture) : request.Title.Trim();
            var conversation = await conversations.CreateConversationAsync(
                publishedGuide.NotebookId,
                title);

            var messageRequest = new SendMessageRequest
            {
                Instructions = request.Instructions ?? string.Empty,
                ModelDeploymentId = request.ModelDeploymentId,
                UseAssistantDefinitionModel = request.UseAssistantDefinitionModel,
                Attachments = request.Attachments,
                ExternalAuthTokens = request.ExternalAuthTokens,
                ClientContext = request.ClientContext
            };

            UsageResponse? usage = null;
            JsonElement? externalToolCalls = null;
            JsonElement? errorPayload = null;
            var pendingClientTool = false;

            try
            {
                await foreach (var ev in conversations.SendMessageStreamAsync(
                                   conversation.Id,
                                   messageRequest,
                                   pubId.ToString(),
                                   authResult.UserIdentity,
                                   authResult.InternalUserId,
                                   ctx.RequestAborted))
                {
                    if (ev.EventType == StreamingEventTypes.ExternalToolCall)
                    {
                        externalToolCalls = TryParseJsonElement(ev.Payload);
                        continue;
                    }

                    if (ev.EventType == StreamingEventTypes.PendingClientTool)
                    {
                        pendingClientTool = true;
                        continue;
                    }

                    if (ev.EventType == StreamingEventTypes.Usage)
                    {
                        usage = TryParseUsage(ev.Payload);
                        continue;
                    }

                    if (ev.EventType == StreamingEventTypes.Error)
                    {
                        errorPayload = TryParseJsonElement(ev.Payload);
                    }
                }
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = "invoke_failed", message = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            if (pendingClientTool)
            {
                return Results.Json(
                    new
                    {
                        error = "pending_client_tool",
                        conversationId = conversation.Id,
                        toolCalls = externalToolCalls
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (errorPayload.HasValue)
            {
                return Results.Json(
                    new { error = "invoke_failed", details = errorPayload.Value },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var assistantMessage = await db.NotebookConversationMessages
                .AsNoTracking()
                .Where(m => m.NotebookConversationId == conversation.Id
                            && m.Role == ChatRole.Assistant
                            && m.IsStreaming != true)
                .OrderByDescending(m => m.TurnIndex)
                .ThenByDescending(m => m.MessageSequence)
                .Select(m => new { m.Id, m.Content })
                .FirstOrDefaultAsync();

            if (assistantMessage == null)
            {
                return Results.Json(
                    new { error = "assistant_message_missing", conversationId = conversation.Id },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(new PublishedGuideInvokeResponse(
                conversation.Id,
                assistantMessage.Id,
                assistantMessage.Content,
                usage));
        })
        .Produces<PublishedGuideInvokeResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status500InternalServerError)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        // Get guide avatar for published guide (public, anonymous access)
        group.MapGet("/{pubId:guid}/avatar", async (
            [FromServices] ApplicationDbContext db,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            Guid pubId,
            HttpContext context) =>
        {
            var publishedGuide = await db.PublishedGuides
                .AsNoTracking()
                .Include(pg => pg.Guide)
                .Where(pg => pg.Id == pubId && pg.Active)
                .FirstOrDefaultAsync();

            if (publishedGuide == null || publishedGuide.Guide.AvatarImageBytes == null)
            {
                return Results.NotFound();
            }

            var limitResult = await costLimits.EnsureWithinLimitsAsync(publishedGuide.NotebookId);
            if (!limitResult.Allowed)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var contentType = DetectImageContentType(publishedGuide.Guide.AvatarImageBytes);
            context.Response.Headers.CacheControl = "public, max-age=3600";
            
            return Results.File(publishedGuide.Guide.AvatarImageBytes, contentType);
        })
        .Produces(StatusCodes.Status200OK, contentType: "image/png")
        .Produces(StatusCodes.Status404NotFound);
    }

    private static string DetectImageContentType(byte[] imageBytes)
    {
        if (imageBytes.Length < 4)
            return "application/octet-stream";

        // PNG signature: 89 50 4E 47
        if (imageBytes[0] == 0x89 && imageBytes[1] == 0x50 && imageBytes[2] == 0x4E && imageBytes[3] == 0x47)
            return "image/png";

        // JPEG signature: FF D8 FF
        if (imageBytes[0] == 0xFF && imageBytes[1] == 0xD8 && imageBytes[2] == 0xFF)
            return "image/jpeg";

        // GIF signature: 47 49 46
        if (imageBytes[0] == 0x47 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46)
            return "image/gif";

        // WebP signature: 52 49 46 46 ... 57 45 42 50
        if (imageBytes.Length >= 12 &&
            imageBytes[0] == 0x52 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46 && imageBytes[3] == 0x46 &&
            imageBytes[8] == 0x57 && imageBytes[9] == 0x45 && imageBytes[10] == 0x42 && imageBytes[11] == 0x50)
            return "image/webp";

        return "application/octet-stream";
    }

    private static JsonElement? TryParseJsonElement(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static UsageResponse? TryParseUsage(string payload)
    {
        try
        {
            var usage = JsonSerializer.Deserialize<UsageEventPayload>(
                payload,
                UsageJsonOptions);

            if (usage == null)
            {
                return null;
            }

            return new UsageResponse
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed record UsageEventPayload(int? PromptTokens, int? CompletionTokens, int? TotalTokens);
}

