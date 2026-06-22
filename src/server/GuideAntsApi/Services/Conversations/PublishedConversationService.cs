using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Commands;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Conversations.Queries;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Services.Conversations.Persistence;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations;

public class PublishedConversationService : IPublishedConversationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConversationPersistence _persistence;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<PublishedConversationService> _logger;
    private readonly IChatModelResolver _chatModelResolver;
    private readonly IConversationQueryService _queryService;
    private readonly IConversationCommandService _commandService;
    private readonly IConversationUndoService _undoService;
    private readonly IConversationHistoryBuilder _historyBuilder;
    private readonly IAttachmentContentService _attachmentContentService;
    private readonly PublishedConversationStreamPolicy _streamPolicy;
    private readonly IConversationStreamEngine _streamEngine;

    public PublishedConversationService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConversationPersistence persistence,
        ILogger<PublishedConversationService> logger,
        IChatModelResolver chatModelResolver,
        IConversationQueryService queryService,
        IConversationCommandService commandService,
        IConversationUndoService undoService,
        IConversationHistoryBuilder historyBuilder,
        IAttachmentContentService attachmentContentService,
        PublishedConversationStreamPolicy streamPolicy,
        IConversationStreamEngine streamEngine,
        IConfiguration? configuration = null)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _persistence = persistence;
        _logger = logger;
        _chatModelResolver = chatModelResolver;
        _queryService = queryService;
        _commandService = commandService;
        _undoService = undoService;
        _historyBuilder = historyBuilder;
        _attachmentContentService = attachmentContentService;
        _streamPolicy = streamPolicy;
        _streamEngine = streamEngine;
        _configuration = configuration;
    }

    public Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title) =>
        _commandService.CreateConversationAsync(notebookId, title);

    public Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId) =>
        _queryService.GetConversationWithMessagesAsync(conversationId);

    public Task UndoLastForConversationAsync(Guid conversationId) =>
        _undoService.UndoLastWithoutLockAsync(conversationId);

    public async IAsyncEnumerable<StreamingEvent> ResumeAfterExternalToolResultsStreamAsync(
        Guid conversationId,
        string? publisherId,
        string? externalUserIdentity,
        Guid? internalUserId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var user = await _streamPolicy.ResolveUserIdentityAsync(internalUserId, externalUserIdentity, cancellationToken);
        var hostUrl = GetHostUrl();

        NotebookConversation dbConversation;
        ConversationTurn dbTurn;
        int currentMessageSequence;
        Guid? notebookConversationMessageId;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbConversation = await db.NotebookConversations
                .Include(c => c.Messages)
                    .ThenInclude(m => m.EditHistory)
                .Include(c => c.Notebook)
                    .ThenInclude(n => n.Guide)
                .Include(c => c.Notebook)
                    .ThenInclude(n => n.Project)
                .Include(c => c.Turns)
                .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
                ?? throw new KeyNotFoundException("Conversation not found");

            dbTurn = db.ConversationTurns
                .Where(t => t.NotebookConversationId == dbConversation.Id)
                .OrderByDescending(t => t.TurnIndex)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No turn found to resume");

            var maxExistingSequence = await db.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == dbConversation.Id && m.TurnIndex == dbTurn.TurnIndex)
                .MaxAsync(m => (int?)m.MessageSequence, cancellationToken) ?? 1;
            currentMessageSequence = maxExistingSequence + 1;
            notebookConversationMessageId = await ResolveTurnUsageMessageIdAsync(
                dbConversation.Id,
                dbTurn.TurnIndex,
                cancellationToken);
        }

        var assistantName = dbConversation.Notebook.Guide?.Name
            ?? throw new InvalidOperationException("Notebook Guide.Name is required for published conversations.");
        var publishedAssistantId = dbConversation.Notebook.GuideId
            ?? dbConversation.Notebook.NotebookTemplateId
            ?? throw new InvalidOperationException("Notebook GuideId is required for published conversations.");

        var previousMessages = await _historyBuilder.BuildPublishedMessagesForAssistantAsync(
            dbConversation,
            assistantName,
            clientContext: null,
            cancellationToken);

        var assistantDefForResume = await AssistantUtility.GetAssistantCreateRequest(assistantName)
            ?? throw new InvalidOperationException($"Assistant definition not found for {assistantName}");
        var resolvedResume = _chatModelResolver.Resolve(assistantDefForResume.Model);

        currentMessageSequence = await ExecuteDeferredServerToolsAsync(
            dbConversation,
            dbTurn,
            assistantName,
            publishedAssistantId,
            notebookConversationMessageId,
            previousMessages,
            publisherId,
            hostUrl,
            currentMessageSequence,
            cancellationToken);

        var runContext = new ConversationStreamRunContext
        {
            Policy = _streamPolicy,
            ConversationId = conversationId,
            Conversation = dbConversation,
            DbTurn = dbTurn,
            TurnIndex = dbTurn.TurnIndex,
            AssistantName = assistantName,
            AssistantId = publishedAssistantId,
            ModelDeploymentId = resolvedResume.ModelId,
            ChatOptions = new ChatRunOptions
            {
                AssistantName = assistantName,
                Instructions = string.Empty,
                DeploymentId = resolvedResume.ModelId,
                ExecutionPolicy = resolvedResume.ExecutionPolicy
            },
            PreviousMessages = previousMessages,
            UserMessageId = notebookConversationMessageId,
            User = user,
            PublisherId = publisherId,
            HostUrl = hostUrl,
            ResumeWithoutNewUserMessage = true,
            UsageContextLabel = "ResumeAfterExternalToolResultsStreamAsync",
            InitialMessageSequence = currentMessageSequence
        };

        var lockHandle = await _streamPolicy.TryAcquireStreamAsync(conversationId, user, cancellationToken);
        await foreach (var ev in _streamEngine.RunStreamAsync(runContext, lockHandle, cancellationToken))
        {
            yield return ev;
        }
    }

    public async IAsyncEnumerable<StreamingEvent> SendMessageStreamAsync(
        Guid conversationId,
        SendMessageRequest request,
        string? publisherId,
        string? externalUserIdentity,
        Guid? internalUserId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Instructions) && (request.Attachments == null || request.Attachments.Count == 0))
        {
            throw new ArgumentException("Instructions required", nameof(request));
        }

        var user = await _streamPolicy.ResolveUserIdentityAsync(internalUserId, externalUserIdentity, cancellationToken);
        var hostUrl = GetHostUrl();

        NotebookConversation dbConversation;
        ConversationTurn dbTurn;
        Guid userMessageId;
        string assistantName;
        Guid runningAssistantId;
        string modelDeploymentId;
        ResolvedExecutionPolicy executionPolicy;
        var previousMessages = new List<ChatMessage>();

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbConversation = await db.NotebookConversations
                .Include(c => c.Messages)
                    .ThenInclude(m => m.EditHistory)
                .Include(c => c.Notebook)
                    .ThenInclude(n => n.Guide)
                .Include(c => c.Notebook)
                    .ThenInclude(n => n.Project)
                .Include(c => c.Turns)
                .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
                ?? throw new KeyNotFoundException("Conversation not found");

            await EnforcePublishedGuideLimitsAsync(db, dbConversation, request, publisherId, cancellationToken);

            var guideId = dbConversation.Notebook.GuideId
                ?? throw new InvalidOperationException("Notebook GuideId is required for published conversations.");
            var guideName = dbConversation.Notebook.Guide?.Name
                ?? throw new InvalidOperationException("Notebook Guide.Name is required for published conversations.");

            (assistantName, runningAssistantId) = await ResolvePublishedAssistantAsync(request, guideId, guideName, cancellationToken);

            modelDeploymentId = request.ModelDeploymentId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modelDeploymentId))
            {
                var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName)
                    ?? throw new InvalidOperationException($"Assistant definition not found for {assistantName}.");
                // An unset assistant model is valid: the resolver supplies the configured default deployment.
                modelDeploymentId = assistantDef.Model ?? string.Empty;
            }

            var resolvedPublished = _chatModelResolver.Resolve(modelDeploymentId);
            modelDeploymentId = resolvedPublished.ModelId;
            executionPolicy = resolvedPublished.ExecutionPolicy;

            previousMessages.AddRange(await _historyBuilder.BuildPublishedMessagesForAssistantAsync(
                dbConversation,
                assistantName,
                request.ClientContext,
                cancellationToken));

            var turnResult = await _persistence.CreateNextTurnAsync(
                new CreateTurnRequest(
                    dbConversation.Id,
                    assistantName,
                    modelDeploymentId,
                    request.Instructions,
                    InitialStatus: "streaming"),
                cancellationToken);
            dbTurn = turnResult.Turn;

            var userResult = await _persistence.CreateUserMessageAsync(
                new CreateUserMessageRequest(
                    dbConversation.Id,
                    turnResult.TurnIndex,
                    MessageSequence: 1,
                    Content: request.Instructions,
                    ModelDeploymentId: modelDeploymentId,
                    UserId: internalUserId,
                    ExternalUserIdentity: externalUserIdentity,
                    AssistantId: runningAssistantId),
                cancellationToken);
            userMessageId = userResult.MessageId;

            if (request.Attachments != null && request.Attachments.Count > 0)
            {
                await _attachmentContentService.AddAttachmentsToUserMessageAsync(
                    db,
                    userResult.MessageId,
                    dbConversation.NotebookId,
                    request.Attachments,
                    cancellationToken);
                foreach (var attachment in request.Attachments)
                {
                    if (!attachment.NotebookFileId.HasValue)
                    {
                        continue;
                    }

                    var messages = await _attachmentContentService.CreateOpenAiMessagesFromNotebookFileAsync(
                        db,
                        attachment.NotebookFileId.Value,
                        cancellationToken);
                    previousMessages.AddRange(messages);
                }
            }
        }

        var runContext = new ConversationStreamRunContext
        {
            Policy = _streamPolicy,
            ConversationId = conversationId,
            Conversation = dbConversation,
            DbTurn = dbTurn,
            TurnIndex = dbTurn.TurnIndex,
            AssistantName = assistantName,
            AssistantId = runningAssistantId,
            ModelDeploymentId = modelDeploymentId,
            ChatOptions = new ChatRunOptions
            {
                AssistantName = assistantName,
                DeploymentId = modelDeploymentId,
                Instructions = request.Instructions,
                ExternalAuthTokens = request.ExternalAuthTokens,
                ExecutionPolicy = executionPolicy
            },
            PreviousMessages = previousMessages,
            UserMessageId = userMessageId,
            User = user,
            PublisherId = publisherId,
            HostUrl = hostUrl,
            UsageContextLabel = "SendMessageStreamAsync"
        };

        var lockHandle = await _streamPolicy.TryAcquireStreamAsync(conversationId, user, cancellationToken);
        await foreach (var ev in _streamEngine.RunStreamAsync(runContext, lockHandle, cancellationToken))
        {
            yield return ev;
        }
    }

    private async Task EnforcePublishedGuideLimitsAsync(
        ApplicationDbContext db,
        NotebookConversation dbConversation,
        SendMessageRequest request,
        string? publisherId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(publisherId) && Guid.TryParse(publisherId, out var pubGuid))
        {
            var publishedGuide = await db.PublishedGuides
                .AsNoTracking()
                .FirstOrDefaultAsync(pg => pg.Id == pubGuid && pg.Active, cancellationToken);

            if (publishedGuide?.MaxUserMessageLength.HasValue == true
                && request.Instructions.Length > publishedGuide.MaxUserMessageLength.Value)
            {
                throw new InvalidOperationException(
                    $"Message exceeds maximum length of {publishedGuide.MaxUserMessageLength} characters");
            }

            if (publishedGuide?.MaxTurns.HasValue == true && dbConversation.Turns.Count >= publishedGuide.MaxTurns.Value)
            {
                throw new InvalidOperationException(
                    $"Conversation has reached maximum of {publishedGuide.MaxTurns} turns");
            }
        }
    }

    private async Task<int> ExecuteDeferredServerToolsAsync(
        NotebookConversation dbConversation,
        ConversationTurn dbTurn,
        string assistantName,
        Guid publishedAssistantId,
        Guid? notebookConversationMessageId,
        List<ChatMessage> previousMessages,
        string? publisherId,
        string? hostUrl,
        int currentMessageSequence,
        CancellationToken cancellationToken)
    {
        List<ChatToolCall> lastToolCalls;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var lastAssistant = await db.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == dbConversation.Id && m.Role == DataModelChatRole.Assistant)
                .OrderByDescending(m => m.Created)
                .FirstOrDefaultAsync(cancellationToken);

            lastToolCalls = new List<ChatToolCall>();
            if (lastAssistant != null && !string.IsNullOrEmpty(lastAssistant.ToolCalls))
            {
                try
                {
                    lastToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(lastAssistant.ToolCalls, JsonOptions) ?? new();
                }
                catch
                {
                    lastToolCalls = new();
                }
            }
        }

        if (lastToolCalls.Count == 0)
        {
            return currentMessageSequence;
        }

        var assistantDefForResume = await AssistantUtility.GetAssistantCreateRequest(assistantName)
            ?? throw new InvalidOperationException($"Assistant definition not found for {assistantName}");

        var sequence = currentMessageSequence;

        MessageAddedEventHandler onMessageAdded = (_, e) =>
        {
            if (cancellationToken.IsCancellationRequested || string.IsNullOrEmpty(e.Role))
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var fileUrlContext = _streamPolicy.BuildFileUrlContext(dbConversation, publisherId, hostUrl);

            if (e.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                var sanitizedAssistant = _streamPolicy.SanitizeAssistantContent(e.Message ?? string.Empty, new Dictionary<string, string>(), fileUrlContext);
                var msg = new NotebookConversationMessage
                {
                    NotebookConversationId = dbConversation.Id,
                    TurnIndex = dbTurn.TurnIndex,
                    MessageSequence = sequence++,
                    Role = DataModelChatRole.Assistant,
                    AssistantId = publishedAssistantId,
                    AssistantName = assistantName,
                    Content = sanitizedAssistant,
                    IsStreaming = false,
                    Created = DateTime.UtcNow,
                    ToolCalls = string.IsNullOrEmpty(e.ToolCallsJson) ? null : e.ToolCallsJson
                };
                db.NotebookConversationMessages.Add(msg);
                var turn = db.ConversationTurns.First(t => t.Id == dbTurn.Id);
                turn.LastUpdated = DateTime.UtcNow;
                db.SaveChanges();
            }
            else if (e.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                var toolMessage = new NotebookConversationMessage
                {
                    NotebookConversationId = dbConversation.Id,
                    TurnIndex = dbTurn.TurnIndex,
                    MessageSequence = sequence++,
                    Role = DataModelChatRole.Tool,
                    AssistantId = publishedAssistantId,
                    Content = e.Message,
                    ToolCallId = e.ToolCallId,
                    FunctionName = e.FunctionName,
                    IsStreaming = false,
                    Created = DateTime.UtcNow
                };
                db.NotebookConversationMessages.Add(toolMessage);
                var turn = db.ConversationTurns.First(t => t.Id == dbTurn.Id);
                turn.LastUpdated = DateTime.UtcNow;
                db.SaveChanges();
            }
        };

        var httpClient = _httpClientFactory.CreateClient();
        var resumeToolContext = new AntRunner.ToolCalling.InvocationContext(
            ProjectId: dbConversation.Notebook.ProjectId,
            NotebookId: dbConversation.NotebookId,
            ConversationId: dbConversation.Id,
            OAuthUserAccessToken: null)
        {
            TurnIndex = dbTurn.TurnIndex,
            AssistantId = publishedAssistantId,
            NotebookConversationMessageId = notebookConversationMessageId
        };

        await AntRunner.Chat.ThreadRun.DoToolCalls(
            assistantDefForResume,
            lastToolCalls,
            previousMessages,
            oAuthUserAccessToken: null,
            httpClient: httpClient,
            messageAdded: onMessageAdded,
            ctx: resumeToolContext);

        return sequence;
    }

    private string? GetHostUrl() =>
        _configuration?["ANTRUNNER_SERVICES_HOST_URL"] ?? Environment.GetEnvironmentVariable("ANTRUNNER_SERVICES_HOST_URL");

    private async Task<Guid?> ResolveTurnUsageMessageIdAsync(
        Guid conversationId,
        int turnIndex,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var userMessageId = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId
                        && m.TurnIndex == turnIndex
                        && m.Role == DataModelChatRole.User)
            .OrderBy(m => m.MessageSequence)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);

        if (userMessageId.HasValue)
        {
            return userMessageId;
        }

        return await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turnIndex)
            .OrderBy(m => m.MessageSequence)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<(string Name, Guid Id)> ResolvePublishedAssistantAsync(
        SendMessageRequest request,
        Guid guideId,
        string guideName,
        CancellationToken cancellationToken)
    {
        var assistantName = string.IsNullOrWhiteSpace(request.AssistantName)
            ? guideName
            : request.AssistantName.Trim();

        if (string.Equals(assistantName, guideName, StringComparison.OrdinalIgnoreCase))
        {
            return (guideName, guideId);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var crewMember = await db.GuideMembers
            .AsNoTracking()
            .Where(gm => gm.GuideId == guideId && gm.Assistant.Name == assistantName)
            .Select(gm => new { gm.AssistantId, Name = gm.Assistant.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (crewMember == null)
        {
            throw new InvalidOperationException(
                $"Assistant '{assistantName}' is not a member of this published guide.");
        }

        return (crewMember.Name, crewMember.AssistantId);
    }
}
