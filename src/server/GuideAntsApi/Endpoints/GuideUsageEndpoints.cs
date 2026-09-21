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


        // Global (no-project) Guide usage report endpoint
        var globalGuideUsageGroup = app.MapGroup("/api/guides/{guideId}/usage")
            .WithTags("Guide Usage (Global)")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        globalGuideUsageGroup.MapGet("/summary", async (
            Guid guideId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var summary = await usageService.GetGuideUsageSummaryAsync(null, guideId, from, to);
                if (summary == null)
                    return Results.NotFound();
                return Results.Ok(summary);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GlobalGetGuideUsageSummary")
        .Produces<GuideUsageSummaryDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        globalGuideUsageGroup.MapGet("/charts", async (
            Guid guideId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var buckets = await usageService.GetGuideUsageDailyBucketsAsync(null, guideId, from, to);
                if (buckets == null)
                    return Results.NotFound();
                return Results.Ok(buckets);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GlobalGetGuideUsageCharts")
        .Produces<List<DailyUsageBucketDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        globalGuideUsageGroup.MapGet("/crew", async (
            Guid guideId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var crew = await usageService.GetGuideUsageCrewAsync(null, guideId, from, to);
                if (crew == null)
                    return Results.NotFound();
                return Results.Ok(crew);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GlobalGetGuideUsageCrew")
        .Produces<GuideUsageCrewDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        globalGuideUsageGroup.MapGet("/conversations", async (
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
                    null,
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
        .WithName("GlobalGetGuideUsageConversations")
        .Produces<GuideUsageConversationsPageDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        globalGuideUsageGroup.MapGet("/api", async (
            Guid guideId,
            DateTime from,
            DateTime to,
            string? source,
            IGuideUsageService usageService) =>
        {
            try
            {
                var report = await usageService.GetGuideApiUsageReportAsync(null, guideId, from, to, source);
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
        .WithName("GlobalGetGuideApiUsage")
        .Produces<GuideApiUsageReportDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Global (no-project) Assistant usage report endpoint
        var globalAssistantUsageGroup = app.MapGroup("/api/assistants/{assistantId}/usage")
            .WithTags("Assistant Usage (Global)")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        globalAssistantUsageGroup.MapGet("/summary", async (
            Guid assistantId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var summary = await usageService.GetGuideUsageSummaryAsync(null, assistantId, from, to);
                if (summary == null)
                    return Results.NotFound();
                return Results.Ok(summary);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GlobalGetAssistantUsageSummary")
        .Produces<GuideUsageSummaryDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        globalAssistantUsageGroup.MapGet("/charts", async (
            Guid assistantId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var buckets = await usageService.GetGuideUsageDailyBucketsAsync(null, assistantId, from, to);
                if (buckets == null)
                    return Results.NotFound();
                return Results.Ok(buckets);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GlobalGetAssistantUsageCharts")
        .Produces<List<DailyUsageBucketDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        globalAssistantUsageGroup.MapGet("/crew", async (
            Guid assistantId,
            DateTime from,
            DateTime to,
            IGuideUsageService usageService) =>
        {
            try
            {
                var crew = await usageService.GetGuideUsageCrewAsync(null, assistantId, from, to);
                if (crew == null)
                    return Results.NotFound();
                return Results.Ok(crew);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GlobalGetAssistantUsageCrew")
        .Produces<GuideUsageCrewDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        globalAssistantUsageGroup.MapGet("/conversations", async (
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
                    null,
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
        .WithName("GlobalGetAssistantUsageConversations")
        .Produces<GuideUsageConversationsPageDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Turn prompt-trace endpoint (system/context/tool definitions drill-down)
        var turnTraceGroup = app.MapGroup("/api/conversations/{conversationId}/turns/{turnIndex}/trace")
            .WithTags("Conversation Messages")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        turnTraceGroup.MapGet("/", async (
            Guid conversationId,
            int turnIndex,
            IGuideUsageService usageService) =>
        {
            try
            {
                var trace = await usageService.GetTurnPromptTraceAsync(conversationId, turnIndex);
                if (trace == null)
                    return Results.NotFound();
                return Results.Ok(trace);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetTurnPromptTrace")
        .Produces<TurnPromptTraceDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);
    }
}
