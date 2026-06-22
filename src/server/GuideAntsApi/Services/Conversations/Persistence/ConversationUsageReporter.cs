using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using GuideAnts.Usage;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Usage;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations.Persistence;

public sealed class ConversationUsageReporter : IConversationUsageReporter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUsageRecorder _usageRecorder;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ConversationUsageReporter> _logger;

    public ConversationUsageReporter(
        IServiceScopeFactory scopeFactory,
        IUsageRecorder usageRecorder,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ConversationUsageReporter> logger)
    {
        _scopeFactory = scopeFactory;
        _usageRecorder = usageRecorder;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task RecordChatCompletionUsageAsync(ChatCompletionUsageRequest request, CancellationToken ct = default)
    {
        try
        {
            var usageService = LlmProviderResolver.ResolveUsageServiceName(request.ModelDeploymentId, _scopeFactory);
            var messageId = await ResolveAssistantMessageIdForChatUsageAsync(request, ct);
            if (messageId == null)
            {
                return;
            }

            var attribution = UsageAttributionHttpContext.TryGet(_httpContextAccessor);

            await _usageRecorder.RecordChatAsync(
                projectId: request.ProjectId,
                notebookId: request.NotebookId,
                service: usageService,
                modelDeploymentId: request.ModelDeploymentId ?? string.Empty,
                metrics: request.Metrics,
                conversationId: request.ConversationId,
                metadataJson: null,
                assistantId: request.AssistantId,
                notebookConversationMessageId: messageId,
                ct: ct,
                publishedGuideId: attribution?.PublishedGuideId,
                sourceChannel: attribution?.SourceChannel,
                externalRequestId: attribution?.ExternalRequestId,
                externalUserIdentity: attribution?.ExternalUserIdentity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to record chat completion usage for conversation {ConversationId} turn {TurnIndex}",
                request.ConversationId,
                request.TurnIndex);
        }
    }

    public async Task RecordToolCallUsageForTurnAsync(ToolTurnUsageRequest request, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var attribution = UsageAttributionHttpContext.TryGet(_httpContextAccessor);

            var toolMessages = await db.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == request.ConversationId
                         && m.TurnIndex == request.TurnIndex
                         && m.Role == DataModelChatRole.Tool
                         && m.FunctionName != null)
                .Select(m => new { m.Id, m.FunctionName, m.ToolCallId, ContentLength = m.Content != null ? m.Content.Length : 0 })
                .ToListAsync(ct);

            if (toolMessages.Count == 0)
            {
                return;
            }

            var toolMessageIds = toolMessages.Select(m => m.Id).ToList();
            var alreadyRecordedIds = await db.UsageEvents
                .Where(u => u.NotebookConversationMessageId != null
                         && toolMessageIds.Contains(u.NotebookConversationMessageId.Value)
                         && u.Category == GuideAntsApi.DataModel.Models.UsageCategory.ToolCall)
                .Select(u => u.NotebookConversationMessageId!.Value)
                .ToListAsync(ct);
            var alreadyRecordedSet = new HashSet<Guid>(alreadyRecordedIds);

            foreach (var toolMsg in toolMessages)
            {
                if (string.Equals(toolMsg.FunctionName, "InvokeAgent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (alreadyRecordedSet.Contains(toolMsg.Id))
                {
                    continue;
                }

                await _usageRecorder.RecordToolCallAsync(
                    projectId: request.ProjectId,
                    notebookId: request.NotebookId,
                    conversationId: request.ConversationId,
                    functionName: toolMsg.FunctionName!,
                    metadataJson: JsonSerializer.Serialize(new
                    {
                        toolCallId = toolMsg.ToolCallId,
                        functionName = toolMsg.FunctionName,
                        contentLength = toolMsg.ContentLength
                    }),
                    assistantId: request.AssistantId,
                    notebookConversationMessageId: toolMsg.Id,
                    ct: ct,
                    publishedGuideId: attribution?.PublishedGuideId,
                    sourceChannel: attribution?.SourceChannel,
                    externalRequestId: attribution?.ExternalRequestId,
                    externalUserIdentity: attribution?.ExternalUserIdentity);
            }
        }
        catch (Exception ex)
        {
            var contextLabel = string.IsNullOrWhiteSpace(request.ContextLabel) ? "turn" : request.ContextLabel;
            _logger.LogWarning(
                ex,
                "Failed to record tool call usage in {ContextLabel} for conversation {ConversationId} turn {TurnIndex}",
                contextLabel,
                request.ConversationId,
                request.TurnIndex);
        }
    }

    public async Task RecordCancelledTurnMarkerUsageAsync(CancelledTurnUsageRequest request, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var attribution = UsageAttributionHttpContext.TryGet(_httpContextAccessor);

            var turnMessageIds = await db.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == request.ConversationId
                         && m.TurnIndex == request.TurnIndex)
                .Select(m => m.Id)
                .ToListAsync(ct);

            if (turnMessageIds.Count == 0)
            {
                return;
            }

            var hasUsageForTurn = await db.UsageEvents
                .Where(u => u.NotebookConversationMessageId != null
                         && turnMessageIds.Contains(u.NotebookConversationMessageId.Value))
                .AnyAsync(ct);

            if (hasUsageForTurn)
            {
                return;
            }

            var messageIdForUsage = request.PreferredAssistantMessageId
                ?? (request.AssistantMessageIds is { Count: > 0 } ids ? ids[^1] : null);

            if (messageIdForUsage == null)
            {
                messageIdForUsage = await db.NotebookConversationMessages
                    .Where(m => m.NotebookConversationId == request.ConversationId
                             && m.TurnIndex == request.TurnIndex
                             && m.Role == DataModelChatRole.Assistant)
                    .OrderByDescending(m => m.Created)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefaultAsync(ct);
            }

            if (messageIdForUsage == null)
            {
                return;
            }

            var usageService = LlmProviderResolver.ResolveUsageServiceName(request.ModelDeploymentId, _scopeFactory);
            var markerMetadata = JsonSerializer.Serialize(new
            {
                cancellationType = "user_cancelled",
                turnIndex = request.TurnIndex
            });

            await _usageRecorder.RecordChatAsync(
                projectId: request.ProjectId,
                notebookId: request.NotebookId,
                service: usageService,
                modelDeploymentId: request.ModelDeploymentId ?? string.Empty,
                metrics: new UsageMetrics(ValueInput: 0, ValueCachedInput: 0, ValueReasoning: 0, ValueOutput: 0),
                conversationId: request.ConversationId,
                metadataJson: markerMetadata,
                assistantId: request.AssistantId,
                notebookConversationMessageId: messageIdForUsage,
                ct: ct,
                publishedGuideId: attribution?.PublishedGuideId,
                sourceChannel: attribution?.SourceChannel,
                externalRequestId: attribution?.ExternalRequestId,
                externalUserIdentity: attribution?.ExternalUserIdentity);
        }
        catch (Exception ex)
        {
            var contextLabel = string.IsNullOrWhiteSpace(request.ContextLabel) ? "turn" : request.ContextLabel;
            _logger.LogWarning(
                ex,
                "Failed to record cancelled turn marker in {ContextLabel} for conversation {ConversationId} turn {TurnIndex}",
                contextLabel,
                request.ConversationId,
                request.TurnIndex);
        }
    }

    private async Task<Guid?> ResolveAssistantMessageIdForChatUsageAsync(
        ChatCompletionUsageRequest request,
        CancellationToken ct)
    {
        if (request.Mode == ConversationUsageMode.Published)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == request.ConversationId && m.Role == DataModelChatRole.Assistant)
                .OrderByDescending(m => m.Created)
                .Select(m => (Guid?)m.Id)
                .FirstOrDefaultAsync(ct);
        }

        if (request.PreferredAssistantMessageId != null)
        {
            return request.PreferredAssistantMessageId;
        }

        if (request.AssistantMessageIds is { Count: > 0 } assistantIds)
        {
            return assistantIds[^1];
        }

        using var privateScope = _scopeFactory.CreateScope();
        var privateDb = privateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await privateDb.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == request.ConversationId
                     && m.TurnIndex == request.TurnIndex
                     && m.Role == DataModelChatRole.Assistant)
            .OrderByDescending(m => m.Created)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);
    }
}
