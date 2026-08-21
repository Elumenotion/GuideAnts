using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Services.Routing;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.SandboxWireApi;

public interface ISandboxWireConversationService
{
    Task<Guid> CreateEphemeralConversationAsync(Guid notebookId, CancellationToken ct = default);

    IAsyncEnumerable<StreamingEvent> SendMessageStreamAsync(
        IWireExecutionContext wireContext,
        Guid conversationId,
        SendMessageRequest request,
        CancellationToken ct = default);

    IAsyncEnumerable<StreamingEvent> ResumeAfterExternalToolResultsStreamAsync(
        IWireExecutionContext wireContext,
        Guid conversationId,
        IReadOnlyList<ChatToolDefinition>? clientToolDefinitions,
        CancellationToken ct = default);
}

public sealed class SandboxWireConversationService : ISandboxWireConversationService
{
    private readonly IPublishedConversationService _publishedConversationService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IChatModelResolver _chatModelResolver;
    private readonly IConversationHistoryBuilder _historyBuilder;
    private readonly PublishedConversationStreamPolicy _streamPolicy;
    private readonly IConversationStreamEngine _streamEngine;
    private readonly ConversationStreamRunRegistry _streamRunRegistry;
    private readonly IConversationPersistence _persistence;
    private readonly IAttachmentContentService _attachmentContentService;
    private readonly IConfiguration? _configuration;

    public SandboxWireConversationService(
        IPublishedConversationService publishedConversationService,
        IServiceScopeFactory scopeFactory,
        IChatModelResolver chatModelResolver,
        IConversationHistoryBuilder historyBuilder,
        PublishedConversationStreamPolicy streamPolicy,
        IConversationStreamEngine streamEngine,
        ConversationStreamRunRegistry streamRunRegistry,
        IConversationPersistence persistence,
        IAttachmentContentService attachmentContentService,
        IConfiguration? configuration = null)
    {
        _publishedConversationService = publishedConversationService;
        _scopeFactory = scopeFactory;
        _chatModelResolver = chatModelResolver;
        _historyBuilder = historyBuilder;
        _streamPolicy = streamPolicy;
        _streamEngine = streamEngine;
        _streamRunRegistry = streamRunRegistry;
        _persistence = persistence;
        _attachmentContentService = attachmentContentService;
        _configuration = configuration;
    }

    public async Task<Guid> CreateEphemeralConversationAsync(Guid notebookId, CancellationToken ct = default)
    {
        var title = $"sandbox-wire-{Guid.NewGuid():N}";
        var created = await _publishedConversationService.CreateConversationAsync(notebookId, title);
        return created.Id;
    }

    public async IAsyncEnumerable<StreamingEvent> SendMessageStreamAsync(
        IWireExecutionContext wireContext,
        Guid conversationId,
        SendMessageRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Instructions) && (request.Attachments == null || request.Attachments.Count == 0))
        {
            throw new ArgumentException("Instructions required", nameof(request));
        }

        var user = await _streamPolicy.ResolveUserIdentityAsync(
            wireContext.InternalUserId,
            wireContext.ExternalUserIdentity,
            ct);
        var hostUrl = GetHostUrl();

        NotebookConversation dbConversation;
        ConversationTurn dbTurn;
        Guid userMessageId;
        var assistantName = wireContext.TargetAssistantName;
        Guid runningAssistantId = wireContext.TargetAssistantId;
        string modelDeploymentId;
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
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct)
                ?? throw new KeyNotFoundException("Conversation not found");

            if (dbConversation.NotebookId != wireContext.NotebookId)
            {
                throw new InvalidOperationException("Conversation does not belong to sandbox wire notebook.");
            }

            modelDeploymentId = request.ModelDeploymentId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modelDeploymentId))
            {
                var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName)
                    ?? throw new InvalidOperationException($"Assistant definition not found for {assistantName}.");
                modelDeploymentId = assistantDef.Model ?? string.Empty;
            }

            var resolvedModel = _chatModelResolver.Resolve(modelDeploymentId);
            modelDeploymentId = resolvedModel.ModelId;

            previousMessages.AddRange(await _historyBuilder.BuildPublishedMessagesForAssistantAsync(
                dbConversation,
                assistantName,
                request.ClientContext,
                request.ClientMessages,
                ct));

            var turnResult = await _persistence.CreateNextTurnAsync(
                new CreateTurnRequest(
                    dbConversation.Id,
                    assistantName,
                    modelDeploymentId,
                    request.Instructions,
                    InitialStatus: "streaming"),
                ct);
            dbTurn = turnResult.Turn;

            var userResult = await _persistence.CreateUserMessageAsync(
                new CreateUserMessageRequest(
                    dbConversation.Id,
                    turnResult.TurnIndex,
                    MessageSequence: 1,
                    Content: request.Instructions,
                    ModelDeploymentId: modelDeploymentId,
                    UserId: wireContext.InternalUserId,
                    ExternalUserIdentity: wireContext.ExternalUserIdentity,
                    AssistantId: runningAssistantId),
                ct);
            userMessageId = userResult.MessageId;

            if (request.Attachments != null && request.Attachments.Count > 0)
            {
                await _attachmentContentService.AddAttachmentsToUserMessageAsync(
                    db,
                    userResult.MessageId,
                    dbConversation.NotebookId,
                    request.Attachments,
                    ct);
                foreach (var attachment in request.Attachments)
                {
                    if (!attachment.NotebookFileId.HasValue)
                    {
                        continue;
                    }

                    var messages = await _attachmentContentService.CreateOpenAiMessagesFromNotebookFileAsync(
                        db,
                        attachment.NotebookFileId.Value,
                        ct);
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
                ClientToolDefinitions = request.ClientToolDefinitions,
                ExecutionPolicy = _chatModelResolver.Resolve(modelDeploymentId).ExecutionPolicy
            },
            PreviousMessages = previousMessages,
            UserMessageId = userMessageId,
            User = user,
            PublisherId = wireContext.PublisherId,
            HostUrl = hostUrl,
            UsageContextLabel = "SandboxWireSendMessageStreamAsync"
        };

        var lockHandle = await _streamPolicy.TryAcquireStreamAsync(conversationId, user, ct);
        var turnId = runContext.DbTurn.Id;
        var workerCts = _streamRunRegistry.Register(turnId);
        await foreach (var ev in _streamEngine.RunStreamAsync(
            runContext,
            lockHandle,
            ct,
            workerCts.Token,
            () => _streamRunRegistry.Unregister(turnId)))
        {
            yield return ev;
        }
    }

    public async IAsyncEnumerable<StreamingEvent> ResumeAfterExternalToolResultsStreamAsync(
        IWireExecutionContext wireContext,
        Guid conversationId,
        IReadOnlyList<ChatToolDefinition>? clientToolDefinitions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in _publishedConversationService.ResumeAfterExternalToolResultsStreamAsync(
            conversationId,
            wireContext.PublisherId,
            wireContext.ExternalUserIdentity,
            wireContext.InternalUserId,
            clientToolDefinitions,
            ct))
        {
            yield return ev;
        }
    }

    private string? GetHostUrl()
    {
        var configured = _configuration?["HostUrl"];
        return string.IsNullOrWhiteSpace(configured) ? null : configured.TrimEnd('/');
    }
}
