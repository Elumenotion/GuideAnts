using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Endpoints;

public static class GuideUsageEndpoints
{
    public static void MapGuideUsageEndpoints(this WebApplication app)
    {
        // Guide usage report endpoint
        var guideUsageGroup = app.MapGroup("/api/projects/{projectId}/guides/{guideId}/usage")
            .WithTags("Guide Usage")
            .RequireAuthorization("RequireAdmin")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        guideUsageGroup.MapGet("/summary", async (
            Guid projectId,
            Guid guideId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var summary = await usageService.GetGuideUsageSummaryAsync(projectId, guideId, from, to);
                if (summary == null)
                    return Results.NotFound();
                return Results.Ok(summary);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetGuideUsageSummary")
        .Produces<GuideUsageSummaryDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        guideUsageGroup.MapGet("/charts", async (
            Guid projectId,
            Guid guideId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var buckets = await usageService.GetGuideUsageDailyBucketsAsync(projectId, guideId, from, to);
                if (buckets == null)
                    return Results.NotFound();
                return Results.Ok(buckets);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetGuideUsageCharts")
        .Produces<List<DailyUsageBucketDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        guideUsageGroup.MapGet("/crew", async (
            Guid projectId,
            Guid guideId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var crew = await usageService.GetGuideUsageCrewAsync(projectId, guideId, from, to);
                if (crew == null)
                    return Results.NotFound();
                return Results.Ok(crew);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetGuideUsageCrew")
        .Produces<GuideUsageCrewDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        guideUsageGroup.MapGet("/conversations", async (
            Guid projectId,
            Guid guideId,
            DateTime from,
            DateTime to,
            int? page,
            int? pageSize,
            IGuideUsageService usageService) =>
        {
            try
            {
                var conversations = await usageService.GetGuideUsageConversationsAsync(
                    projectId,
                    guideId,
                    from,
                    to,
                    page ?? 1,
                    pageSize ?? 100);
                if (conversations == null)
                    return Results.NotFound();
                return Results.Ok(conversations);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetGuideUsageConversations")
        .Produces<GuideUsageConversationsPageDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        guideUsageGroup.MapGet("/api", async (
            Guid projectId,
            Guid guideId,
            DateTime from,
            DateTime to,
            string? source,
            IGuideUsageService usageService) =>
        {
            try
            {
                var report = await usageService.GetGuideApiUsageReportAsync(projectId, guideId, from, to, source);
                if (report == null)
                {
                    return Results.NotFound();
                }

                return Results.Ok(report);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetGuideApiUsage")
        .Produces<GuideApiUsageReportDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Assistant usage report endpoint (same service, different route for assistants)
        var assistantUsageGroup = app.MapGroup("/api/projects/{projectId}/assistants/{assistantId}/usage")
            .WithTags("Assistant Usage")
            .RequireAuthorization("RequireAdmin")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        assistantUsageGroup.MapGet("/summary", async (
            Guid projectId,
            Guid assistantId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var summary = await usageService.GetGuideUsageSummaryAsync(projectId, assistantId, from, to);
                if (summary == null)
                    return Results.NotFound();
                return Results.Ok(summary);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetAssistantUsageSummary")
        .Produces<GuideUsageSummaryDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        assistantUsageGroup.MapGet("/charts", async (
            Guid projectId,
            Guid assistantId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var buckets = await usageService.GetGuideUsageDailyBucketsAsync(projectId, assistantId, from, to);
                if (buckets == null)
                    return Results.NotFound();
                return Results.Ok(buckets);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetAssistantUsageCharts")
        .Produces<List<DailyUsageBucketDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        assistantUsageGroup.MapGet("/crew", async (
            Guid projectId,
            Guid assistantId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var crew = await usageService.GetGuideUsageCrewAsync(projectId, assistantId, from, to);
                if (crew == null)
                    return Results.NotFound();
                return Results.Ok(crew);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetAssistantUsageCrew")
        .Produces<GuideUsageCrewDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        assistantUsageGroup.MapGet("/conversations", async (
            Guid projectId,
            Guid assistantId,
            DateTime from,
            DateTime to,
            int? page,
            int? pageSize,
            IGuideUsageService usageService) =>
        {
            try
            {
                var conversations = await usageService.GetGuideUsageConversationsAsync(
                    projectId,
                    assistantId,
                    from,
                    to,
                    page ?? 1,
                    pageSize ?? 100);
                if (conversations == null)
                    return Results.NotFound();
                return Results.Ok(conversations);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetAssistantUsageConversations")
        .Produces<GuideUsageConversationsPageDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        assistantUsageGroup.MapGet("/api", async (
            Guid projectId,
            Guid assistantId,
            DateTime from,
            DateTime to,
            string? source,
            IGuideUsageService usageService) =>
        {
            try
            {
                var report = await usageService.GetGuideApiUsageReportAsync(projectId, assistantId, from, to, source);
                if (report == null)
                {
                    return Results.NotFound();
                }

                return Results.Ok(report);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetAssistantApiUsage")
        .Produces<GuideApiUsageReportDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Invocation details endpoint
        var invocationsGroup = app.MapGroup("/api/invocations")
            .WithTags("Invocations")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        invocationsGroup.MapGet("/{invocationId}", async (
            Guid invocationId,
            bool? includeMessages,
            IGuideUsageService usageService) =>
        {
            try
            {
                var invocation = await usageService.GetInvocationAsync(
                    invocationId, 
                    includeMessages ?? true);
                if (invocation == null)
                    return Results.NotFound();
                return Results.Ok(invocation);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetInvocation")
        .Produces<InvocationNodeDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // All turn invocations for a conversation (single request)
        var allTurnsGroup = app.MapGroup("/api/conversations/{conversationId}/invocations")
            .WithTags("Invocations")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        allTurnsGroup.MapGet("/", async (
            Guid conversationId,
            IGuideUsageService usageService) =>
        {
            try
            {
                var trees = await usageService.GetAllTurnInvocationsAsync(conversationId);
                return Results.Ok(trees);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetAllTurnInvocations")
        .Produces<List<TurnInvocationTreeDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Turn invocations tree endpoint (single turn - kept for backwards compatibility)
        var conversationsGroup = app.MapGroup("/api/conversations/{conversationId}/turns/{turnIndex}/invocations")
            .WithTags("Invocations")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        conversationsGroup.MapGet("/", async (
            Guid conversationId,
            int turnIndex,
            IGuideUsageService usageService) =>
        {
            try
            {
                var tree = await usageService.GetTurnInvocationsAsync(conversationId, turnIndex);
                if (tree == null)
                    return Results.NotFound();
                return Results.Ok(tree);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetTurnInvocations")
        .Produces<TurnInvocationTreeDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Turn messages endpoint (for viewing conversation messages with invocation drill-down)
        var turnMessagesGroup = app.MapGroup("/api/conversations/{conversationId}/turns/{turnIndex}/messages")
            .WithTags("Conversation Messages")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        turnMessagesGroup.MapGet("/", async (
            Guid conversationId,
            int turnIndex,
            IGuideUsageService usageService) =>
        {
            try
            {
                var messages = await usageService.GetTurnMessagesAsync(conversationId, turnIndex);
                if (messages == null)
                    return Results.NotFound();
                return Results.Ok(messages);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetTurnMessages")
        .Produces<TurnMessagesDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);
    }
}
