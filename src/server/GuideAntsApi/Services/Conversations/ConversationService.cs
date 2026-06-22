using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using System.Runtime.CompilerServices;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Commands;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Conversations.Queries;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Services.Conversations;

public class ConversationService : IConversationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationPersistence _persistence;
    private readonly IChatModelResolver _chatModelResolver;
    private readonly IConversationQueryService _queryService;
    private readonly IConversationCommandService _commandService;
    private readonly IConversationHistoryBuilder _historyBuilder;
    private readonly IAttachmentContentService _attachmentContentService;
    private readonly INotebookFileService? _notebookFileService;
    private readonly IConversationUndoService _undoService;
    private readonly PrivateConversationStreamPolicy _streamPolicy;
    private readonly IConversationStreamEngine _streamEngine;
    private readonly IToolOAuthService? _toolOAuthService;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        IServiceScopeFactory scopeFactory,
        IConversationPersistence persistence,
        IChatModelResolver chatModelResolver,
        IConversationQueryService queryService,
        IConversationCommandService commandService,
        IConversationHistoryBuilder historyBuilder,
        IAttachmentContentService attachmentContentService,
        INotebookFileService? notebookFileService,
        IConversationUndoService undoService,
        PrivateConversationStreamPolicy streamPolicy,
        IConversationStreamEngine streamEngine,
        ILogger<ConversationService> logger,
        IToolOAuthService? toolOAuthService = null)
    {
        _scopeFactory = scopeFactory;
        _persistence = persistence;
        _chatModelResolver = chatModelResolver;
        _queryService = queryService;
        _commandService = commandService;
        _historyBuilder = historyBuilder;
        _attachmentContentService = attachmentContentService;
        _notebookFileService = notebookFileService;
        _undoService = undoService;
        _streamPolicy = streamPolicy;
        _streamEngine = streamEngine;
        _logger = logger;
        _toolOAuthService = toolOAuthService;
    }

    public Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId) =>
        _queryService.GetConversationByIdAsync(conversationId);

    public Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId) =>
        _queryService.GetConversationWithMessagesAsync(conversationId);

    public Task UndoLastForConversationAsync(Guid conversationId) =>
        _undoService.UndoLastForConversationAsync(conversationId);

    public Task UndoForConversationAsync(Guid conversationId, Guid messageId) =>
        _undoService.UndoForConversationAsync(conversationId, messageId);

    public Task EditMessageAsync(Guid messageId, string newContent) =>
        _commandService.EditMessageAsync(messageId, newContent);

    public Task<IReadOnlyList<NotebookConversationListDto>> GetListAsync(Guid notebookId) =>
        _queryService.GetListAsync(notebookId);

    public Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title) =>
        _commandService.CreateConversationAsync(notebookId, title);

    public Task RenameConversationAsync(Guid conversationId, string title) =>
        _commandService.RenameConversationAsync(conversationId, title);

    public Task DeleteConversationAsync(Guid conversationId) =>
        _commandService.DeleteConversationAsync(conversationId);

    public Task<PagedUserConversationsDto> GetUserConversationsAsync(UserConversationsQuery query) =>
        _queryService.GetUserConversationsAsync(query);

    public async IAsyncEnumerable<StreamingEvent> SendMessageStreamToConversationAsync(
        Guid conversationId,
        SendMessageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var user = await _streamPolicy.ResolveUserIdentityAsync(internalUserId: null, externalUserIdentity: null, CancellationToken.None);
        var lockHandle = await _streamPolicy.TryAcquireStreamAsync(conversationId, user, CancellationToken.None);

        ConversationStreamRunContext runContext;
        try
        {
            var setupCt = CancellationToken.None;
            var loaded = await LoadStreamMetadataAsync(conversationId, request, user, setupCt);

            await CreateTurnAndUserMessageAsync(loaded, setupCt);
            if (!await _persistence.SetTurnStatusAsync(loaded.DbTurn!.Id, "streaming", ct: setupCt))
            {
                _logger.LogWarning(
                    "Turn {TurnId} disappeared before streaming status update in conversation {ConversationId}",
                    loaded.DbTurn.Id,
                    conversationId);
            }

            await _streamPolicy.OnTurnCreatedAsync(
                conversationId,
                new StreamTurnCreatedInfo(
                    loaded.TurnIndex,
                    request.Instructions,
                    loaded.AssistantName,
                    user),
                setupCt);

            await PopulateStreamHistoryAsync(loaded, setupCt);
            await ProcessAttachmentsAsync(loaded, setupCt);

            runContext = BuildRunContext(_streamPolicy, loaded, user);
        }
        catch
        {
            await lockHandle.ReleaseAsync(CancellationToken.None);
            throw;
        }

        await foreach (var ev in _streamEngine.RunStreamAsync(runContext, lockHandle, cancellationToken))
        {
            yield return ev;
        }
    }

    private sealed class StreamSendContext
    {
        public required Guid ConversationId { get; init; }
        public required NotebookConversation Conversation { get; init; }
        public required SendMessageRequest Request { get; init; }
        public required StreamUserIdentity User { get; init; }
        public required string AssistantName { get; init; }
        public string? ModelDeploymentId { get; init; }
        public ResolvedExecutionPolicy? ExecutionPolicy { get; init; }
        public required List<ChatMessage> PreviousMessages { get; set; }
        public Guid? AssistantId { get; init; }
        public required Dictionary<string, string> ExternalAuthTokens { get; set; }
        public int TurnIndex { get; set; }
        public ConversationTurn? DbTurn { get; set; }
        public NotebookConversationMessage? UserMessage { get; set; }
    }

    private async Task<StreamSendContext> LoadStreamMetadataAsync(
        Guid conversationId,
        SendMessageRequest request,
        StreamUserIdentity user,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Instructions) && (request.Attachments == null || request.Attachments.Count == 0))
        {
            throw new ArgumentException("Instructions required", nameof(request));
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conv = await db.NotebookConversations
            .Include(c => c.Messages)
                .ThenInclude(m => m.EditHistory)
            .Include(c => c.Notebook)
                .ThenInclude(n => n.Guide)
            .Include(c => c.Notebook)
                .ThenInclude(n => n.Project)
            .Include(c => c.Turns)
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new KeyNotFoundException("Conversation not found");

        var assistantName = string.IsNullOrWhiteSpace(request.AssistantName) ? "assistant" : request.AssistantName;
        var modelDeploymentId = request.ModelDeploymentId;
        if (string.IsNullOrWhiteSpace(modelDeploymentId))
        {
            var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName)
                ?? throw new InvalidOperationException($"Assistant definition not found for {assistantName}.");
            modelDeploymentId = assistantDef.Model;
        }

        var requestedModelDeploymentId = modelDeploymentId;
        var resolvedModel = _chatModelResolver.Resolve(modelDeploymentId);
        modelDeploymentId = resolvedModel.ModelId;
        _logger.LogInformation(
            "Conversation chat model resolved. ConversationId={ConversationId}, AssistantName={AssistantName}, RequestedModelId={RequestedModelId}, ResolvedModelId={ResolvedModelId}, ReferenceKind={ReferenceKind}, Authority={Authority}, ParameterKeys=[{ParameterKeys}]",
            LogValueSanitizer.Sanitize(conversationId),
            LogValueSanitizer.Sanitize(assistantName),
            LogValueSanitizer.Sanitize(string.IsNullOrWhiteSpace(requestedModelDeploymentId) ? "(unset)" : requestedModelDeploymentId),
            LogValueSanitizer.Sanitize(resolvedModel.ModelId),
            LogValueSanitizer.Sanitize(resolvedModel.ReferenceKind),
            LogValueSanitizer.Sanitize(resolvedModel.ExecutionPolicy.Authority),
            LogValueSanitizer.Sanitize(string.Join(", ", resolvedModel.ExecutionPolicy.Parameters.Keys)));

        var assistantId = await db.Assistants
            .Where(a => a.Name == assistantName && a.IsActive)
            .OrderBy(a => a.IsGlobal)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);

        return new StreamSendContext
        {
            ConversationId = conversationId,
            Conversation = conv,
            Request = request,
            User = user,
            AssistantName = assistantName,
            ModelDeploymentId = modelDeploymentId,
            ExecutionPolicy = resolvedModel.ExecutionPolicy,
            PreviousMessages = [],
            AssistantId = assistantId,
            ExternalAuthTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task PopulateStreamHistoryAsync(StreamSendContext ctx, CancellationToken ct)
    {
        ctx.PreviousMessages = await _historyBuilder.PrepareMessagesForAssistantAsync(
            ctx.Conversation,
            ctx.AssistantName,
            ctx.User.UserId!.Value,
            ct);

        ctx.ExternalAuthTokens = _toolOAuthService != null
            ? await _toolOAuthService.ResolveExternalAuthTokensForAssistantAsync(
                ctx.User.UserId!.Value,
                ctx.Conversation.Notebook.ProjectId,
                ctx.AssistantId,
                ctx.AssistantName,
                ct)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task CreateTurnAndUserMessageAsync(StreamSendContext ctx, CancellationToken ct)
    {
        var turnResult = await _persistence.CreateNextTurnAsync(
            new CreateTurnRequest(
                ctx.ConversationId,
                ctx.AssistantName,
                ctx.ModelDeploymentId,
                ctx.Request.Instructions),
            ct);

        var userResult = await _persistence.CreateUserMessageAsync(
            new CreateUserMessageRequest(
                ctx.Conversation.Id,
                turnResult.TurnIndex,
                MessageSequence: 1,
                Content: ctx.Request.Instructions,
                ModelDeploymentId: ctx.ModelDeploymentId,
                UserId: ctx.User.UserId,
                ExternalUserIdentity: null,
                AssistantId: ctx.AssistantId),
            ct);

        ctx.TurnIndex = turnResult.TurnIndex;
        ctx.DbTurn = turnResult.Turn;
        ctx.UserMessage = userResult.Message;
    }

    private async Task ProcessAttachmentsAsync(StreamSendContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Attachments == null || ctx.Request.Attachments.Count == 0)
        {
            return;
        }

        foreach (var attachment in ctx.Request.Attachments)
        {
            if (attachment.NotebookFileId.HasValue)
            {
                await _attachmentContentService.AddAttachmentsToUserMessageAsync(
                    ctx.UserMessage!.Id,
                    ctx.Conversation.NotebookId,
                    [attachment],
                    ct);

                var messages = await _attachmentContentService.CreateOpenAiMessagesFromNotebookFileAsync(attachment.NotebookFileId.Value, ct);
                foreach (var message in messages)
                {
                    ctx.PreviousMessages.Add(message);
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(attachment.RelativePath))
            {
                continue;
            }

            if (_notebookFileService == null)
            {
                _logger.LogWarning(
                    "Skipping path attachment because notebook file service is unavailable. relativePath={RelativePath}",
                    LogValueSanitizer.Sanitize(attachment.RelativePath));
                continue;
            }

            var normalizedPath = attachment.RelativePath.Replace("\\", "/").TrimStart('/');
            var file = await _notebookFileService.GetFileAsync(
                ctx.Conversation.Notebook.ProjectId,
                ctx.Conversation.NotebookId,
                normalizedPath);
            if (file == null)
            {
                _logger.LogWarning(
                    "Path attachment file not found for conversation send. notebookId={NotebookId} relativePath={RelativePath}",
                    ctx.Conversation.NotebookId,
                    LogValueSanitizer.Sanitize(normalizedPath));
                continue;
            }

            await file.Value.Stream.DisposeAsync();
            var attachmentPath = BuildAttachmentPathForChat(normalizedPath);
            ctx.PreviousMessages.Add(new ChatMessage(AntRunner.Chat.Abstractions.ChatRole.User, $"Attachment: {attachmentPath}"));
        }
    }

    private static string BuildAttachmentPathForChat(string relativePath)
    {
        if (relativePath.StartsWith("Output/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("Output\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(relativePath);
        }

        if (relativePath.StartsWith("Runs/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("Runs\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(relativePath);
        }

        return $"../{relativePath.Replace("\\", "/").TrimStart('/')}";
    }

    private static ConversationStreamRunContext BuildRunContext(
        IConversationStreamPolicy policy,
        StreamSendContext ctx,
        StreamUserIdentity user) =>
        new()
        {
            Policy = policy,
            ConversationId = ctx.ConversationId,
            Conversation = ctx.Conversation,
            DbTurn = ctx.DbTurn!,
            TurnIndex = ctx.TurnIndex,
            AssistantName = ctx.AssistantName,
            AssistantId = ctx.AssistantId,
            ModelDeploymentId = ctx.ModelDeploymentId,
            ChatOptions = new ChatRunOptions
            {
                AssistantName = ctx.AssistantName,
                DeploymentId = ctx.ModelDeploymentId,
                Instructions = ctx.Request.Instructions,
                oAuthUserAccessToken = ctx.ExternalAuthTokens.FirstOrDefault().Value,
                ExternalAuthTokens = ctx.ExternalAuthTokens,
                ExecutionPolicy = ctx.ExecutionPolicy
            },
            PreviousMessages = ctx.PreviousMessages,
            UserMessageId = ctx.UserMessage?.Id,
            User = user
        };
}
