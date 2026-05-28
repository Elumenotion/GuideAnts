using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using GuideAntsApi.Services.Core;
using System.Text.Json;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Options;
using System.Text;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GuideAnts.Usage;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations;

public class ConversationService : IConversationService
{

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITurnManager _turnManager;
    private readonly IConversationBroadcastHub _broadcastHub;
    private readonly IDistributedConversationLock _distributedLock;

    private readonly INotebookFileService? _notebookFileService;
    private readonly INotebookFileSyncService? _notebookFileSyncService;
    private readonly IMarkdownExtractionService? _markdownExtractionService;
    private readonly ILogger<ConversationService> _logger;
    private readonly IConfiguration? _configuration;
    private readonly IUsageRecorder _usageRecorder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _storagePath;
    private readonly IChatCompletionClientFactory _chatClientFactory;

    // Conversation-level locking for streaming operations (local concurrency only)
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _conversationLocks = new();

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IContextOptionsService _contextOptionsService;
    private readonly IOptions<MarkdownAttachmentOptions> _markdownAttachmentOptions;
    private readonly IChatModelResolver _chatModelResolver;

    public ConversationService(IHttpClientFactory httpClientFactory,
        ITurnManager turnManager,
        IConversationBroadcastHub broadcastHub,
        IDistributedConversationLock distributedLock,
        IServiceScopeFactory scopeFactory,
        IUsageRecorder usageRecorder,
        IChatCompletionClientFactory chatClientFactory,
        IContextOptionsService contextOptionsService,
        IOptions<MarkdownAttachmentOptions> markdownAttachmentOptions,
        IChatModelResolver chatModelResolver,
        INotebookFileService? notebookFileService = null,
        INotebookFileSyncService? notebookFileSyncService = null,
        IMarkdownExtractionService? markdownExtractionService = null,
        ILogger<ConversationService>? logger = null,
        IConfiguration? configuration = null)
    {
        _httpClientFactory = httpClientFactory;
        _turnManager = turnManager;
        _broadcastHub = broadcastHub;
        _distributedLock = distributedLock ?? throw new ArgumentNullException(nameof(distributedLock));
        _notebookFileService = notebookFileService;
        _notebookFileSyncService = notebookFileSyncService;
        _markdownExtractionService = markdownExtractionService;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration;
        _usageRecorder = usageRecorder ?? throw new ArgumentNullException(nameof(usageRecorder));
        _scopeFactory = scopeFactory;
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _contextOptionsService = contextOptionsService ?? throw new ArgumentNullException(nameof(contextOptionsService));
        _markdownAttachmentOptions = markdownAttachmentOptions ?? throw new ArgumentNullException(nameof(markdownAttachmentOptions));
        _chatModelResolver = chatModelResolver ?? throw new ArgumentNullException(nameof(chatModelResolver));
        _storagePath = configuration?["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
    }

    public async Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conv = await db.NotebookConversations
            .Include(c => c.Notebook)
            .Include(c => c.Messages)
                .ThenInclude(m => m.EditHistory)
            .Include(c => c.Messages)
                .ThenInclude(m => m.Attachments)
                    .ThenInclude(a => a.NotebookFile)
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conv == null) return null;


        return ToDto(conv);
    }

    /// <summary>
    /// Gets a conversation with all its messages, optimized for performance.
    /// Uses projection to avoid cartesian explosion and minimize data transfer.
    /// </summary>
    public async Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // First check access
        var projectId = await db.NotebookConversations
            .Where(c => c.Id == conversationId)
            .Select(c => c.Notebook.ProjectId)
            .FirstOrDefaultAsync();

        if (projectId == Guid.Empty) return null;


        // Use READ UNCOMMITTED for this read-only query — prevents blocking by retention cleanup
        // lock escalation on NotebookConversationMessages/ConversationTurns tables.
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED");

        // Get the basic conversation data with optimized projection.
        // Use AsSingleQuery() so the conversation Id filters the entire query; with global SplitQuery
        // the split collection query was scanning all NotebookConversationMessages then joining to
        // this conversation, causing timeouts.
        var conversationData = await db.NotebookConversations
            .Where(c => c.Id == conversationId)
            .AsSingleQuery()
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.Created,
                AssistantName = c.Turns.OrderByDescending(t => t.TurnIndex).FirstOrDefault() != null
                    ? c.Turns.OrderByDescending(t => t.TurnIndex).FirstOrDefault()!.AssistantName
                    : c.Messages.Where(m => m.Role == DataModelChatRole.Assistant).OrderByDescending(m => m.Created).FirstOrDefault() != null
                        ? c.Messages.Where(m => m.Role == DataModelChatRole.Assistant).OrderByDescending(m => m.Created).FirstOrDefault()!.AssistantName
                        : null,
                LastActivity = c.Messages.Any() ? c.Messages.Max(m => m.Created) : c.Created,
                Messages = c.Messages.Where(m => m.IsStreaming != true).OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence)
                    .Select(m => new
                    {
                        m.Id,
                        m.Role,
                        m.Content,
                        UserId = m.UserId ?? m.LastEditedByUserId,
                        m.AssistantName,
                        m.IsEdited,
                        m.LastEditedAt,
                        m.Created,
                        OriginalContent = m.EditHistory != null ? m.EditHistory.OriginalContent : null,
                        m.ToolCalls, // Raw JSON string
                        m.ThinkingBlocksJson,
                        m.ToolCallId,
                        m.FunctionName,
                        m.MessageContentType,
                        m.TurnIndex,
                        m.MessageSequence,
                        Attachments = m.Attachments.OrderBy(a => a.OrderIndex)
                            .Select(a => new
                            {
                                a.NotebookFileId,
                                FileName = a.NotebookFile != null ? Path.GetFileName(a.NotebookFile.RelativePath ?? "unknown") : "unknown",
                                FileType = a.NotebookFile != null ? DetermineFileTypeString(a.NotebookFile.RelativePath ?? "") : "other",
                                FileSize = a.NotebookFile != null ? a.NotebookFile.FileSize : 0,
                                a.Type
                            }).ToList()
                    }).ToList(),
                Turns = c.Turns.Select(t => new
                {
                    t.TurnIndex,
                    t.FilesCreated,
                    t.FilesModified
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (conversationData == null)
            return null;

        // Build turn file maps for attaching to last assistant message of each turn
        var turnFilesCreated = new Dictionary<int, List<string>>();
        var turnFilesModified = new Dictionary<int, List<string>>();
        foreach (var turn in conversationData.Turns)
        {
            if (!string.IsNullOrEmpty(turn.FilesCreated))
            {
                try
                {
                    var files = JsonSerializer.Deserialize<List<string>>(turn.FilesCreated, _jsonOptions);
                    if (files != null && files.Count > 0)
                        turnFilesCreated[turn.TurnIndex] = files;
                }
                catch { /* ignore parse errors */ }
            }
            if (!string.IsNullOrEmpty(turn.FilesModified))
            {
                try
                {
                    var files = JsonSerializer.Deserialize<List<string>>(turn.FilesModified, _jsonOptions);
                    if (files != null && files.Count > 0)
                        turnFilesModified[turn.TurnIndex] = files;
                }
                catch { /* ignore parse errors */ }
            }
        }

        // Find the last assistant message for each turn (highest MessageSequence with Role == Assistant)
        var lastAssistantMessagePerTurn = conversationData.Messages
            .Where(m => m.Role == DataModelChatRole.Assistant)
            .GroupBy(m => m.TurnIndex)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.MessageSequence).First().Id);

        // Convert to final DTOs, parsing tool calls as needed
        var messageDtos = new List<MessageDto>();
        foreach (var msg in conversationData.Messages)
        {
            messageDtos.AddRange(BuildThinkingMessageDtos(
                msg.Id,
                msg.Role,
                msg.AssistantName,
                msg.Created,
                msg.ThinkingBlocksJson,
                msg.TurnIndex));

            // Parse tool calls if present
            IReadOnlyList<ToolCallDto>? toolCalls = null;
            if (!string.IsNullOrEmpty(msg.ToolCalls))
            {
                try
                {
                    var openAiToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(msg.ToolCalls, _jsonOptions);
                    if (openAiToolCalls != null)
                    {
                        toolCalls = openAiToolCalls.Select(tc => new ToolCallDto(
                            tc.Id,
                            tc.Type.ToString(),
                            new ToolCallFunctionDto(
                                tc.Function.Name,
                                tc.Function.Arguments.ToString()
                            )
                        )).ToList();
                    }
                }
                catch
                {
                    // If deserialization fails, leave toolCalls as null
                }
            }

            // Convert attachments
            var attachments = msg.Attachments.Select(a => new AttachedFileDto(
                a.NotebookFileId,
                a.FileName,
                a.FileType,
                a.FileSize,
                null, // No preview URL for now
                a.Type
            )).ToList();

            // Check if this is the last assistant message for its turn
            var isLastAssistantInTurn = lastAssistantMessagePerTurn.TryGetValue(msg.TurnIndex, out var lastAssistantId)
                && lastAssistantId == msg.Id;

            // Get turn files if this is the last assistant message
            List<string>? filesCreated = null;
            List<string>? filesModified = null;
            if (isLastAssistantInTurn)
            {
                turnFilesCreated.TryGetValue(msg.TurnIndex, out filesCreated);
                turnFilesModified.TryGetValue(msg.TurnIndex, out filesModified);
            }

            messageDtos.Add(new MessageDto(
                msg.Id,
                msg.Role,
                msg.Content,
                msg.UserId,
                msg.AssistantName,
                msg.IsEdited,
                msg.LastEditedAt,
                msg.Created,
                msg.OriginalContent,
                toolCalls,
                msg.ToolCallId,
                msg.FunctionName,
                attachments,
                msg.MessageContentType,
                null, // AttachedNotebookFileId (deprecated)
                msg.TurnIndex,
                filesCreated,
                filesModified
            ));
        }

        // Filter out duplicate assistant messages (legacy data where streaming created duplicates)
        var filteredMessageDtos = FilterDuplicateAssistantMessages(
            messageDtos,
            m => m.Role,
            m => m.TurnIndex ?? 0,
            m => m.Content,
            m => m.ToolCalls != null && m.ToolCalls.Count > 0
        );

        return new NotebookConversationWithMessagesDto(
            conversationData.Id,
            conversationData.Title ?? "Untitled",
            conversationData.AssistantName,
            conversationData.Created,
            conversationData.LastActivity,
            filteredMessageDtos
        );
    }

    public async Task UndoLastForConversationAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        _logger.LogCritical("🚨 UNDO LAST called for conversation {ConversationId}", conversationId);
        var conv = await db.NotebookConversations
            .Include(c => c.Notebook)
            .Include(c => c.Messages)
                .ThenInclude(m => m.EditHistory)
            .FirstOrDefaultAsync(c => c.Id == conversationId)
            ?? throw new KeyNotFoundException("Conversation not found");


        // Find the last turn that has a user message
        var lastUserMessage = conv.Messages
            .Where(m => m.Role == DataModelChatRole.User)
            .OrderByDescending(m => m.TurnIndex)
            .ThenByDescending(m => m.MessageSequence)
            .FirstOrDefault();

        if (lastUserMessage == null) return;

        // Remove all messages from this turn onwards
        var messagesToRemove = conv.Messages
            .Where(m => m.TurnIndex >= lastUserMessage.TurnIndex)
            .ToList();

        // Remove all turns from this turn index onwards
        var turnsToRemove = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId)
            .Where(t => t.TurnIndex >= lastUserMessage.TurnIndex)
            .ToListAsync();

        _logger.LogCritical("🚨 UNDO LAST removing {MessageCount} messages and {TurnCount} turns from turn {TurnIndex} onwards in conversation {ConversationId}",
            messagesToRemove.Count, turnsToRemove.Count, lastUserMessage.TurnIndex, conversationId);

        db.NotebookConversationMessages.RemoveRange(messagesToRemove);
        db.ConversationTurns.RemoveRange(turnsToRemove);
        await db.SaveChangesAsync();

        // CRITICAL: Release conversation lock if it exists (fixes stuck "locked state" bug)
        await _distributedLock.ReleaseLockAsync(conversationId, CancellationToken.None);
        _logger.LogInformation("Released conversation lock during undo for {ConversationId}", conversationId);

        // Broadcast turn_removed event to all observers
        await _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.TurnRemoved, JsonSerializer.Serialize(new
            {
                turnIndex = lastUserMessage.TurnIndex,
                messagesRemoved = messagesToRemove.Count,
                timestamp = DateTime.UtcNow
            }, _jsonOptions)));

        _logger.LogCritical("🚨 UNDO LAST completed for conversation {ConversationId}", conversationId);
    }

    public async Task UndoForConversationAsync(Guid conversationId, Guid messageId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        _logger.LogCritical("🚨 UNDO FOR MESSAGE called for conversation {ConversationId}, message {MessageId}", conversationId, messageId);
        var conv = await db.NotebookConversations
            .Include(c => c.Notebook)
            .Include(c => c.Messages)
                .ThenInclude(m => m.EditHistory)
            .FirstOrDefaultAsync(c => c.Id == conversationId)
            ?? throw new KeyNotFoundException("Conversation not found");


        var targetMessage = conv.Messages.FirstOrDefault(m => m.Id == messageId)
                    ?? throw new KeyNotFoundException("Message not found");

        // Remove all messages from this turn onwards
        var messagesToRemove = conv.Messages
            .Where(m => m.TurnIndex >= targetMessage.TurnIndex)
            .ToList();

        // Remove all turns from this turn index onwards
        var turnsToRemove = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId)
            .Where(t => t.TurnIndex >= targetMessage.TurnIndex)
            .ToListAsync();

        _logger.LogCritical("🚨 UNDO FOR MESSAGE removing {MessageCount} messages and {TurnCount} turns from turn {TurnIndex} onwards in conversation {ConversationId}",
            messagesToRemove.Count, turnsToRemove.Count, targetMessage.TurnIndex, conversationId);

        db.NotebookConversationMessages.RemoveRange(messagesToRemove);
        db.ConversationTurns.RemoveRange(turnsToRemove);
        await db.SaveChangesAsync();

        // CRITICAL: Release conversation lock if it exists (fixes stuck "locked state" bug)
        await _distributedLock.ReleaseLockAsync(conversationId, CancellationToken.None);
        _logger.LogInformation("Released conversation lock during undo for {ConversationId}", conversationId);

        // Broadcast turn_removed event to all observers
        await _broadcastHub.BroadcastToConversationAsync(conversationId,
            new StreamingEvent(StreamingEventTypes.TurnRemoved, JsonSerializer.Serialize(new
            {
                turnIndex = targetMessage.TurnIndex,
                messagesRemoved = messagesToRemove.Count,
                timestamp = DateTime.UtcNow
            }, _jsonOptions)));

        _logger.LogCritical("🚨 UNDO FOR MESSAGE completed for conversation {ConversationId}", conversationId);
    }

    // Placeholder for the new Edit method
    public async Task EditMessageAsync(Guid messageId, string newContent)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var message = await db.NotebookConversationMessages
            .Include(m => m.NotebookConversation)
                .ThenInclude(nc => nc.Notebook)
            .Include(m => m.EditHistory)
            .FirstOrDefaultAsync(m => m.Id == messageId)
            ?? throw new KeyNotFoundException("Message not found");


        // Only allow editing assistant messages
        if (message.Role != DataModelChatRole.Assistant)
        {
            throw new InvalidOperationException("Only assistant messages can be edited");
        }

        // If this is the first edit, create history record
        if (!message.IsEdited && message.EditHistory == null)
        {
            var editHistory = new MessageEditHistory
            {
                MessageId = messageId,
                OriginalContent = message.Content,
                OriginalToolCalls = message.ToolCalls,
                FirstEditedByUserId = null,
                FirstEditedAt = DateTime.UtcNow
            };
            db.MessageEditHistories.Add(editHistory);
        }

        // Update the message
        message.Content = newContent;
        message.IsEdited = true;
        message.LastEditedByUserId = null;
        message.LastEditedAt = DateTime.UtcNow;

        // Clear tool calls since user is providing new content
        message.ToolCalls = null;

        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<NotebookConversationListDto>> GetListAsync(Guid notebookId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var projectId = await db.Notebooks
            .Where(n => n.Id == notebookId)
            .Select(n => n.ProjectId)
            .FirstOrDefaultAsync();

        if (projectId == Guid.Empty) return [];


        // Use READ UNCOMMITTED — this read-only list must not be blocked by retention cleanup
        // locks on NotebookConversations or UsageEvents for this notebook.
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED");

        var convs = await db.NotebookConversations
            .Where(c => c.NotebookId == notebookId)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.Created,
                // Get last activity from UsageEvents - EF translates to efficient SQL
                LastActivity = db.UsageEvents
                    .Where(e => e.ConversationId == c.Id)
                    .Max(e => (DateTime?)e.Created) ?? c.Created
            })
            .OrderByDescending(c => c.LastActivity)
            .Select(c => new NotebookConversationListDto(c.Id, c.Title, c.Created, c.LastActivity))
            .ToListAsync();
        return convs;
    }

    public async Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notebook = await db.Notebooks.FirstOrDefaultAsync(n => n.Id == notebookId);
        if (notebook == null) throw new KeyNotFoundException("Notebook not found");


        var conv = new NotebookConversation
        {
            NotebookId = notebookId,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim()
        };
        db.NotebookConversations.Add(conv);
        await db.SaveChangesAsync();
        return new NotebookConversationListDto(conv.Id, conv.Title, conv.Created, conv.Created);
    }

    public async Task RenameConversationAsync(Guid conversationId, string title)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conv = await db.NotebookConversations
            .Include(c => c.Notebook)
            .FirstOrDefaultAsync(c => c.Id == conversationId)
            ?? throw new KeyNotFoundException();


        conv.Title = string.IsNullOrWhiteSpace(title) ? conv.Title : title.Trim();
        await db.SaveChangesAsync();
    }

    public async Task DeleteConversationAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conv = await db.NotebookConversations
            .Include(c => c.Notebook)
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conv == null) return;


        _logger.LogCritical("🚨 DELETING CONVERSATION {ConversationId} - THIS WILL CASCADE DELETE ALL MESSAGES!", conversationId);
        db.NotebookConversations.Remove(conv);

        try
        {
            await db.SaveChangesAsync();
            _logger.LogCritical("🚨 CONVERSATION {ConversationId} DELETED - ALL MESSAGES GONE!", conversationId);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Handle concurrent deletion: if conversation was already deleted by another request,
            // verify it's gone and treat as success (desired outcome achieved)
            var stillExists = await db.NotebookConversations
                .AnyAsync(c => c.Id == conversationId);

            if (!stillExists)
            {
                _logger.LogInformation("Conversation {ConversationId} was already deleted by another request - treating as success", conversationId);
                return;
            }

            // If it still exists, something else went wrong - rethrow
            throw;
        }
    }



    public async IAsyncEnumerable<StreamingEvent> SendMessageStreamToConversationAsync(Guid conversationId, SendMessageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var userName = "User";

        // Validate user identity before proceeding - fail securely if uncertain
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new UnauthorizedAccessException("User identity could not be established for conversation streaming");
        }

        // Try to acquire distributed lock (cross-container coordination)
        var lockResult = await _distributedLock.TryAcquireLockAsync(conversationId, userName, cancellationToken);

        switch (lockResult.Status)
        {
            case LockAcquisitionStatus.ConversationNotFound:
                throw new KeyNotFoundException($"Conversation {conversationId} not found");

            case LockAcquisitionStatus.AlreadyLocked:
                throw new InvalidOperationException($"Conversation is locked by {lockResult.LockedByUserName}");

            case LockAcquisitionStatus.RaceCondition:
                throw new InvalidOperationException("Conversation is locked by another user");

            case LockAcquisitionStatus.Acquired:
                break;
        }

        // Get local semaphore for in-process concurrency
        var lockSemaphore = _conversationLocks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));

        StreamSendContext? ctx = null;
        bool streamingSucceeded = false;
        bool lockSemaphoreAcquired = false;
        bool conversationLockEventSent = false;
        bool distributedLockReleased = false;
        bool emitCompleteAfterUnlock = false;

        try
        {
            await lockSemaphore.WaitAsync(cancellationToken);
            lockSemaphoreAcquired = true;

            // Notify all subscribers that this conversation is now locked for streaming
            await _broadcastHub.BroadcastToConversationAsync(conversationId,
                new StreamingEvent(StreamingEventTypes.ConversationLocked, JsonSerializer.Serialize(new
                {
                    activeUserId = Guid.Empty,
                    activeUserName = userName,
                    timestamp = DateTime.UtcNow
                }, _jsonOptions)));
            conversationLockEventSent = true;

            // 1. Build initial context (loads DB entities, validates input, prepares previous messages)
            ctx = await LoadStreamContextAsync(conversationId, request, cancellationToken);

            // 2. Create DB turn, system message (if needed) and user message
            await CreateTurnAndUserMessageAsync(ctx, cancellationToken);

            // Set turn status to streaming
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var turn = await db.ConversationTurns
                    .FirstAsync(t => t.Id == ctx.DbTurn!.Id, cancellationToken);
                turn.Status = "streaming";
                turn.LastUpdated = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            // Broadcast turn creation to observers
            await _broadcastHub.BroadcastToConversationAsync(conversationId,
                new StreamingEvent(StreamingEventTypes.TurnCreated, JsonSerializer.Serialize(new
                {
                    turnIndex = ctx.TurnIndex,
                    userId = Guid.Empty,
                    userName = userName,
                    userMessage = ctx.Request.Instructions,
                    assistantName = ctx.AssistantName,
                    timestamp = DateTime.UtcNow
                }, _jsonOptions)));

            // 3. Handle attachments (DB + OpenAI messages)
            await ProcessAttachmentsAsync(ctx, cancellationToken);

            // 4. Build ChatRunner options
            var chatOptions = BuildChatRunOptions(ctx);

            // 5. Initialise channel/throttling & spin background runner with broadcasting
            var infra = InitStreamingInfrastructure(ctx, cancellationToken);
            StartChatRunnerBackgroundTask(ctx, chatOptions, infra, cancellationToken, conversationId);

            // Notify that streaming has started
            await _broadcastHub.BroadcastToConversationAsync(conversationId,
                new StreamingEvent(StreamingEventTypes.StreamingStarted, JsonSerializer.Serialize(new
                {
                    assistantName = ctx.AssistantName,
                    turnIndex = ctx.TurnIndex,
                    timestamp = DateTime.UtcNow
                }, _jsonOptions)));

            // 6. Forward events to caller and broadcast to observers
            await foreach (var ev in infra.Channel.Reader.ReadAllAsync(cancellationToken))
            {
                // Broadcast all events to conversation observers
                await _broadcastHub.BroadcastToConversationAsync(conversationId, ev);

                // Yield to the active streaming client
                yield return ev;
            }

            // If we reach here, streaming completed successfully
            streamingSucceeded = true;

            // Mark turn as completed
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var turn = await db.ConversationTurns
                    .FirstAsync(t => t.Id == ctx.DbTurn!.Id, cancellationToken);
                turn.Status = "completed";
                turn.LastUpdated = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            // Do not block unlock on a full notebook walk. File-producing tools already reconcile
            // synchronously before they return; if a turn reported changes, queue a best-effort
            // follow-up sync for any stragglers instead of holding the conversation open.
            if (_notebookFileSyncService != null && ctx.Conversation.Notebook != null && ctx.TurnReportedFileChanges)
            {
                try
                {
                    await _notebookFileSyncService.QueueNotebookSyncAsync(ctx.Conversation.Notebook.Id, CancellationToken.None);
                }
                catch (Exception syncEx)
                {
                    // Never let sync errors break the conversation flow
                    _logger.LogWarning(syncEx, "Failed to queue notebook sync for {NotebookId} after turn completion", ctx.Conversation.Notebook.Id);
                }
            }

            // Important ordering: this complete event is emitted only after unlock cleanup runs.
            emitCompleteAfterUnlock = true;
        }
        finally
        {
            // Mark turn as cancelled if streaming failed and we have a turn
            if (!streamingSucceeded && ctx?.DbTurn != null)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var turn = await db.ConversationTurns
                        .FirstOrDefaultAsync(t => t.Id == ctx.DbTurn.Id, CancellationToken.None);
                    if (turn != null && turn.Status == "streaming")
                    {
                        turn.Status = "cancelled";
                        turn.LastUpdated = DateTime.UtcNow;
                        await db.SaveChangesAsync(CancellationToken.None);
                    }
                }
                catch (Exception updateEx)
                {
                    _logger.LogError(updateEx, "Error updating turn status to cancelled");
                }
            }

            if (lockSemaphoreAcquired)
            {
                try
                {
                    lockSemaphore.Release();
                }
                catch (Exception semaphoreReleaseEx)
                {
                    _logger.LogWarning(semaphoreReleaseEx, "Failed to release local semaphore for {ConversationId}", conversationId);
                }
            }

            // Release distributed lock (must happen regardless of other cleanup errors)
            try
            {
                await _distributedLock.ReleaseLockAsync(conversationId, CancellationToken.None);
                distributedLockReleased = true;
                _logger.LogInformation("Released conversation lock for {ConversationId}", conversationId);
            }
            catch (Exception releaseEx)
            {
                _logger.LogError(releaseEx, "Failed to release distributed conversation lock for {ConversationId}", conversationId);
            }

            if (conversationLockEventSent && distributedLockReleased)
            {
                try
                {
                    await _broadcastHub.BroadcastToConversationAsync(conversationId,
                        new StreamingEvent(StreamingEventTypes.ConversationUnlocked, JsonSerializer.Serialize(new
                        {
                            timestamp = DateTime.UtcNow
                        }, _jsonOptions)));
                }
                catch (Exception unlockBroadcastEx)
                {
                    _logger.LogWarning(unlockBroadcastEx, "Failed to broadcast conversation unlock for {ConversationId}", conversationId);
                }
            }
        }

        if (emitCompleteAfterUnlock && distributedLockReleased)
        {
            var completeEvent = new StreamingEvent(StreamingEventTypes.Complete, "{}");
            await _broadcastHub.BroadcastToConversationAsync(conversationId, completeEvent);
            yield return completeEvent;
        }
    }

    private static string DetermineEventType(string role, string message)
    {
        return role.ToLowerInvariant() switch
        {
            "user" => StreamingEventTypes.UserMessage,
            "assistant" => StreamingEventTypes.AssistantMessage,
            "tool" => StreamingEventTypes.ToolResult,
            "system" => StreamingEventTypes.SystemMessage,
            _ => StreamingEventTypes.Message
        };
    }

    private async Task<string?> GetTemplateDefaultModelAsync(Guid templateId)
    {
        try
        {
            // Create a new scope to get a fresh DbContext instance to avoid concurrency issues
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Look up guide by ID - Assistants table is source of truth
            var guide = await db.Assistants
                .AsNoTracking()
                .Include(a => a.Model)
                .Where(a => a.Id == templateId && a.Kind == AssistantKind.Guide && a.IsActive)
                .FirstOrDefaultAsync();

            return guide?.Model!.ModelId;
        }
        catch (Exception ex)
        {
            // Log the error but don't fail the chat - just use the default
            System.Diagnostics.Debug.WriteLine($"Failed to read guide default model: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Prepares messages for the specified assistant, handling assistant switching logic
    /// </summary>
    private async Task<List<ChatMessage>> PrepareMessagesForAssistantAsync(NotebookConversation conv, string assistantName, Guid userId, CancellationToken cancellationToken = default)
    {
        // Turns should already be loaded in LoadStreamContextAsync to avoid concurrent DbContext access
        // If for some reason they're not loaded, we need to handle this gracefully
        if (!conv.Turns.Any() && conv.Messages.Any())
        {
            // Turns collection might not be loaded in some edge cases
            // We'll proceed without loading them to avoid DbContext concurrency issues
            _logger.LogWarning("Turns collection not loaded for conversation {ConversationId}", conv.Id);
        }

        var lastTurn = conv.Turns.OrderByDescending(t => t.TurnIndex).FirstOrDefault();
        var isAssistantSwitch = lastTurn != null && lastTurn.AssistantName != assistantName;
        var isNewConversation = conv.Messages.Count == 0;

        // Always start with the assistant's instructions as the first message
        var messages = new List<ChatMessage>();
        var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName);
        if (assistantDef != null)
        {
            // 1. Assistant instructions (primary system prompt)
            if (!string.IsNullOrWhiteSpace(assistantDef.Instructions))
            {
                messages.Add(new ChatMessage(ChatMessageRole.System, assistantDef.Instructions));
            }

            // 2. Context options message comes **after** instructions to improve attention
            var ctxMsg = await _contextOptionsService.BuildContextMessageAsync(
                assistantDef,
                conv.Notebook?.ProjectId ?? Guid.Empty,
                conv.Notebook?.Id ?? Guid.Empty,
                conv.Id);
            if (!string.IsNullOrEmpty(ctxMsg))
            {
                messages.Add(new ChatMessage(ChatMessageRole.System, ctxMsg));
            }
        }

        // If this is a new conversation, just return the system message
        if (isNewConversation)
        {
            return messages;
        }

        // If this is an assistant switch, apply transition logic with proper filtering
        if (isAssistantSwitch)
        {
            var switchMessages = await ApplyAssistantSwitchLogicAsync(conv, assistantName);
            // For assistant switch we want context options last, so we need to move it:
            // Remove previously added context message (if any) and re-append after switch messages.
            string? ctxContent = null;
            var ctxIndex = messages.FindIndex(m => m.Role == ChatMessageRole.System && m.GetText().StartsWith("{\"contextOptions\""));
            if (ctxIndex >= 0)
            {
                ctxContent = messages[ctxIndex].GetText();
                messages.RemoveAt(ctxIndex);
            }

            messages.AddRange(switchMessages);

            if (ctxContent != null)
            {
                messages.Add(new ChatMessage(ChatMessageRole.System, ctxContent));
            }

            return messages;
        }

        // For continuing conversations with the same assistant
        var conversationMessages = await BuildOpenAiMessagesAsync(conv, assistantName, cancellationToken);
        messages.AddRange(conversationMessages);
        return messages;
    }



    /// <summary>
    /// Applies assistant switching logic similar to Conversation.ChangeAssistant
    /// </summary>
    private async Task<List<ChatMessage>> ApplyAssistantSwitchLogicAsync(NotebookConversation conv, string newAssistantName)
    {
        // Filter out duplicate assistant messages (legacy data where streaming created duplicates)
        var dedupedMessages = FilterDuplicateAssistantMessages(
            conv.Messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls)
        );

        // Get the new assistant definition
        var assistantDef = await AssistantUtility.GetAssistantCreateRequest(newAssistantName);
        if (assistantDef == null)
        {
            // If we can't find the assistant definition, include all messages including tool messages
            // Tool messages contain essential context like file URLs and results
            return dedupedMessages
                .OrderBy(m => m.TurnIndex)
                .ThenBy(m => m.MessageSequence)
                .Select(ToChatMessage)
                .ToList();
        }

        // Filter messages for assistant switching:
        // - Keep user messages (always)
        // - Keep assistant messages WITHOUT tool calls (so new assistant can see conversation flow)
        // - Keep tool messages from the NEW assistant (so it can see results of its own tool calls)
        // - Exclude tool messages from other assistants (avoid confusion)
        // IMPORTANT: Order chronologically first to maintain conversation flow

        // First pass: collect all tool call IDs from assistant messages that belong to the NEW assistant
        var validToolCallIds = new HashSet<string>();
        foreach (var m in dedupedMessages.Where(m => m.Role == DataModelChatRole.Assistant && !string.IsNullOrEmpty(m.ToolCalls)))
        {
            if (m.AssistantName == newAssistantName)
            {
                try
                {
                    var toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls!, _jsonOptions);
                    if (toolCalls != null)
                    {
                        foreach (var tc in toolCalls)
                        {
                            validToolCallIds.Add(tc.Id);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize tool calls for message {MessageId}", m.Id);
                }
            }
        }

        var filteredMessages = new List<ChatMessage>();
        foreach (var m in dedupedMessages.OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence))
        {
            // Include tool response messages only if they belong to the NEW assistant
            if (m.Role == DataModelChatRole.Tool)
            {
                if (string.IsNullOrEmpty(m.ToolCallId) || !validToolCallIds.Contains(m.ToolCallId))
                {
                    continue; // Skip tool responses from other assistants
                }
            }
            else if (m.Role == DataModelChatRole.Assistant && !string.IsNullOrEmpty(m.ToolCalls))
            {
                // Include assistant messages with tool calls only if they're from the new assistant
                if (m.AssistantName != newAssistantName)
                {
                    continue; // Skip tool call messages from other assistants
                }
            }
            else if (!(m.Role == DataModelChatRole.User || m.Role == DataModelChatRole.Assistant))
            {
                continue; // Skip other message types (system, etc.)
            }

            // Check for attachments via new MessageAttachment system
            List<MessageAttachment> attachments;
            using (var attachmentScope = _scopeFactory.CreateScope())
            {
                var attachmentDb = attachmentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                attachments = await attachmentDb.MessageAttachments
                .Include(ma => ma.NotebookFile)
                .Where(ma => ma.MessageId == m.Id)
                .OrderBy(ma => ma.OrderIndex)
                .ToListAsync();
            }

            if (attachments.Count > 0)
            {
                // Create OpenAI message with multiple content items
                var contents = new List<ChatContent>();

                // Add text content if present
                if (!string.IsNullOrEmpty(m.Content))
                {
                    contents.Add(new ChatContent(m.Content));
                }

                // Add file contents
                foreach (var attachment in attachments)
                {
                    var fileContents = await CreateOpenAiContentFromNotebookFileAsync(attachment.NotebookFileId);
                    contents.AddRange(fileContents);
                }

                if (contents.Count > 0)
                {
                    var role = m.Role switch
                    {
                        DataModelChatRole.User => ChatMessageRole.User,
                        DataModelChatRole.Assistant => ChatMessageRole.Assistant,
                        DataModelChatRole.Tool => ChatMessageRole.Tool,
                        _ => ChatMessageRole.System
                    };
                    var thinkingBlocks = role == ChatMessageRole.Assistant
                        ? DeserializeThinkingBlocks(m.ThinkingBlocksJson)
                        : null;

                    filteredMessages.Add(new ChatMessage(role, contents, null, thinkingBlocks));
                    continue;
                }
            }

            filteredMessages.Add(ToChatMessage(m));
        }

        // Create new message list for assistant switching (system message will be added by caller)
        var newMessages = new List<ChatMessage>();

        // Add the filtered conversation history first
        newMessages.AddRange(filteredMessages);

        // Add context message about the assistant transition AFTER the previous conversation
        // This ensures the handoff context appears after the previous assistant's last reply
        // Always add handoff message during assistant switches, even if filtered messages is empty
        if (conv.Messages.Any())
        {
            newMessages.Add(new ChatMessage(ChatMessageRole.System,
                    "The previous messages between the user and assistant above are from a conversation with a different assistant. " +
                    "Use them to understand the conversation context, but follow the system messages that were provided at the start of this message sequence."));
        }

        return newMessages;
    }




    /// <summary>
    /// Builds OpenAI messages list from database messages, handling multiple attachments per message.
    /// Filters tool messages to only include those from the specified assistant.
    /// </summary>
    private async Task<List<ChatMessage>> BuildOpenAiMessagesAsync(NotebookConversation conv, string assistantName, CancellationToken cancellationToken)
    {
        var list = new List<ChatMessage>();

        // Filter out duplicate assistant messages (legacy data where streaming created duplicates)
        var filteredMessages = FilterDuplicateAssistantMessages(
            conv.Messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls)
        );

        // First pass: collect all tool call IDs from assistant messages that will be included in the API request
        var validToolCallIds = new HashSet<string>();
        foreach (var dbMsg in filteredMessages.Where(m => m.Role == DataModelChatRole.Assistant && !string.IsNullOrEmpty(m.ToolCalls)))
        {
            // Only collect tool call IDs from assistant messages that will actually be included
            // This includes: user messages, assistant messages without tool calls, and assistant messages with tool calls from current assistant
            bool willBeIncluded = dbMsg.AssistantName == assistantName;

            if (willBeIncluded)
            {
                try
                {
                    var toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(dbMsg.ToolCalls!, _jsonOptions);
                    if (toolCalls != null)
                    {
                        foreach (var tc in toolCalls)
                        {
                            validToolCallIds.Add(tc.Id);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize tool calls for message {MessageId}", dbMsg.Id);
                }
            }
        }

        foreach (var dbMsg in filteredMessages.OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence))
        {
            // Filter tool response messages to only include those with valid tool call IDs
            if (dbMsg.Role == DataModelChatRole.Tool)
            {
                if (string.IsNullOrEmpty(dbMsg.ToolCallId) || !validToolCallIds.Contains(dbMsg.ToolCallId))
                {
                    continue; // Skip orphaned tool responses
                }
            }

            // Filter assistant messages with tool calls to only include those from the current assistant
            if (dbMsg.Role == DataModelChatRole.Assistant && !string.IsNullOrEmpty(dbMsg.ToolCalls))
            {
                if (dbMsg.AssistantName != assistantName)
                {
                    continue; // Skip tool call messages from other assistants
                }
            }

            // Include partial/streaming assistant messages for context continuity
            // (external DTOs filter these out, but internal resume needs them)

            // NEW: Handle messages with multiple attachments
            List<MessageAttachment> attachments;
            using (var attachmentScope = _scopeFactory.CreateScope())
            {
                var attachmentDb = attachmentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                attachments = await attachmentDb.MessageAttachments
                .Include(ma => ma.NotebookFile)
                .Where(ma => ma.MessageId == dbMsg.Id)
                .OrderBy(ma => ma.OrderIndex)
                .ToListAsync(cancellationToken);
            }

            if (attachments.Count > 0)
            {
                // Create OpenAI message with multiple content items
                var contents = new List<ChatContent>();

                // Add text content if present
                if (!string.IsNullOrEmpty(dbMsg.Content))
                {
                    contents.Add(new ChatContent(dbMsg.Content));
                }

                // Add file contents
                foreach (var attachment in attachments)
                {
                    var fileContents = await CreateOpenAiContentFromNotebookFileAsync(attachment.NotebookFileId, cancellationToken);
                    contents.AddRange(fileContents);
                }

                if (contents.Count > 0)
                {
                    var role = dbMsg.Role switch
                    {
                        DataModelChatRole.User => ChatMessageRole.User,
                        DataModelChatRole.Assistant => ChatMessageRole.Assistant,
                        DataModelChatRole.Tool => ChatMessageRole.Tool,
                        _ => ChatMessageRole.System
                    };

                    list.Add(new ChatMessage(role, contents));
                    continue;
                }
            }

            // Default: Regular text message (includes partial streaming content)
            list.Add(ToChatMessage(dbMsg));
        }
        return list;
    }

    #region Mapping helpers

    /// <summary>
    /// Filters out duplicate assistant messages that have the same content but no ToolCalls
    /// when another message in the same turn has the same content WITH ToolCalls.
    /// This handles legacy data where streaming created duplicate rows.
    /// </summary>
    private static List<T> FilterDuplicateAssistantMessages<T>(
        IEnumerable<T> messages,
        Func<T, DataModelChatRole> getRole,
        Func<T, int> getTurnIndex,
        Func<T, string?> getContent,
        Func<T, bool> hasToolCalls)
    {
        var messageList = messages.ToList();

        // Build set of (turn, content) pairs that have a message WITH ToolCalls
        var turnContentWithToolCalls = new HashSet<(int turn, string content)>();
        foreach (var m in messageList.Where(m => getRole(m) == DataModelChatRole.Assistant && hasToolCalls(m)))
        {
            var key = (getTurnIndex(m), getContent(m)?.Trim() ?? "");
            turnContentWithToolCalls.Add(key);
        }

        // Filter out duplicates: skip assistant messages without ToolCalls if same content exists with ToolCalls
        var result = new List<T>();
        foreach (var m in messageList)
        {
            if (getRole(m) == DataModelChatRole.Assistant && !hasToolCalls(m))
            {
                var key = (getTurnIndex(m), getContent(m)?.Trim() ?? "");
                if (turnContentWithToolCalls.Contains(key))
                {
                    // Skip: this message has no ToolCalls but another with same content does
                    continue;
                }
            }
            result.Add(m);
        }

        return result;
    }

    private static MessageDto ToDto(NotebookConversationMessage m)
    {
        // Parse tool calls if present
        IReadOnlyList<ToolCallDto>? toolCalls = null;
        if (!string.IsNullOrEmpty(m.ToolCalls))
        {
            try
            {
                var openAiToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls, _jsonOptions);
                if (openAiToolCalls != null)
                {
                    toolCalls = openAiToolCalls.Select(tc => new ToolCallDto(
                        tc.Id,
                        tc.Type.ToString(),
                        new ToolCallFunctionDto(
                            tc.Function.Name,
                            tc.Function.Arguments.ToString()
                        )
                    )).ToList();
                }
            }
            catch (JsonException)
            {
                // If deserialization fails, leave toolCalls as null
            }
        }

        // NEW: Include multiple attachments in DTO
        var attachments = m.Attachments?
            .OrderBy(a => a.OrderIndex)
            .Select(a => new AttachedFileDto(
                a.NotebookFileId,
                Path.GetFileName(a.NotebookFile?.RelativePath ?? "unknown"),
                DetermineFileTypeString(a.NotebookFile?.RelativePath ?? ""),
                a.NotebookFile?.FileSize ?? 0,
                null, // No preview URL for now
                a.Type
            ))
            .ToList() ?? new List<AttachedFileDto>();

        return new MessageDto(
            m.Id,
            m.Role,
            m.Content,
            m.UserId ?? m.LastEditedByUserId,
            m.AssistantName,
            m.IsEdited,
            m.LastEditedAt,
            m.Created,
            m.EditHistory?.OriginalContent,
            toolCalls,
            m.ToolCallId,
            m.FunctionName,
            attachments, // NEW: Multiple attachments
            m.MessageContentType
        );
    }

    private static ConversationDto ToDto(NotebookConversation c)
    {
        var messages = c.Messages.Where(m => m.IsStreaming != true).ToList();

        // Filter out duplicate assistant messages (legacy data where streaming created duplicates)
        var filteredMessages = FilterDuplicateAssistantMessages(
            messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls)
        );

        var orderedMessages = filteredMessages
            .OrderBy(m => m.TurnIndex)
            .ThenBy(m => m.MessageSequence)
            .ToList();

        var messageDtos = new List<MessageDto>();
        foreach (var message in orderedMessages)
        {
            messageDtos.AddRange(BuildThinkingMessageDtos(message));
            messageDtos.Add(ToDto(message));
        }

        return new ConversationDto(
            c.NotebookId,
            c.Created,
            messageDtos
        );
    }

    private static ChatMessage ToChatMessage(NotebookConversationMessage m)
    {
        var role = m.Role switch
        {
            DataModelChatRole.User => ChatMessageRole.User,
            DataModelChatRole.Assistant => ChatMessageRole.Assistant,
            DataModelChatRole.Tool => ChatMessageRole.Tool,
            _ => ChatMessageRole.System
        };

        if (role == ChatMessageRole.Assistant)
        {
            var thinkingBlocks = DeserializeThinkingBlocks(m.ThinkingBlocksJson);

            // CRITICAL FIX: Assistant messages need their tool calls for proper LLM context
            if (!string.IsNullOrEmpty(m.ToolCalls))
            {
                try
                {
                    var toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls, _jsonOptions);
                    if (toolCalls != null && toolCalls.Count > 0)
                    {
                        var content = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
                        return new ChatMessage(role, content, toolCalls, thinkingBlocks);
                    }
                }
                catch (JsonException ex)
                {
                    // Log the error but don't fail the message processing
                    // The message will still be included without tool calls
                    System.Diagnostics.Debug.WriteLine($"Failed to deserialize tool calls: {ex.Message}");
                }
            }

            var assistantContent = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
            return new ChatMessage(role, assistantContent, null, thinkingBlocks);
        }

        if (role == ChatMessageRole.Tool && m.ToolCallId != null && m.FunctionName != null)
        {
            var toolContent = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
            return new ChatMessage(m.ToolCallId, m.FunctionName, toolContent);
        }

        // User/System
        return new ChatMessage(role, m.Content);
    }

    private static IReadOnlyList<MessageDto> BuildThinkingMessageDtos(NotebookConversationMessage message)
    {
        if (message.Role != DataModelChatRole.Assistant)
        {
            return [];
        }

        var thinkingBlocks = DeserializeThinkingBlocks(message.ThinkingBlocksJson);
        if (thinkingBlocks is not { Count: > 0 })
        {
            return [];
        }

        var results = new List<MessageDto>(thinkingBlocks.Count);
        for (var i = 0; i < thinkingBlocks.Count; i++)
        {
            var content = FormatThinkingDisplay(thinkingBlocks[i]);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            results.Add(new MessageDto(
                BuildThinkingMessageId(message.Id, i),
                DataModelChatRole.Assistant,
                content,
                null,
                message.AssistantName,
                false,
                null,
                message.Created,
                null,
                null,
                null,
                null,
                [],
                MessageContentType.Text,
                null,
                message.TurnIndex,
                null,
                null
            ));
        }

        return results;
    }

    private static IReadOnlyList<MessageDto> BuildThinkingMessageDtos(
        Guid messageId,
        DataModelChatRole role,
        string? assistantName,
        DateTime created,
        string? thinkingBlocksJson,
        int? turnIndex)
    {
        if (role != DataModelChatRole.Assistant)
        {
            return [];
        }

        var thinkingBlocks = DeserializeThinkingBlocks(thinkingBlocksJson);
        if (thinkingBlocks is not { Count: > 0 })
        {
            return [];
        }

        var results = new List<MessageDto>(thinkingBlocks.Count);
        for (var i = 0; i < thinkingBlocks.Count; i++)
        {
            var content = FormatThinkingDisplay(thinkingBlocks[i]);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            results.Add(new MessageDto(
                BuildThinkingMessageId(messageId, i),
                DataModelChatRole.Assistant,
                content,
                null,
                assistantName,
                false,
                null,
                created,
                null,
                null,
                null,
                null,
                [],
                MessageContentType.Text,
                null,
                turnIndex,
                null,
                null
            ));
        }

        return results;
    }

    private static Guid BuildThinkingMessageId(Guid sourceId, int index)
    {
        var sourceBytes = sourceId.ToByteArray();
        var indexBytes = BitConverter.GetBytes(index);
        var input = new byte[sourceBytes.Length + indexBytes.Length];
        Buffer.BlockCopy(sourceBytes, 0, input, 0, sourceBytes.Length);
        Buffer.BlockCopy(indexBytes, 0, input, sourceBytes.Length, indexBytes.Length);
        var hash = System.Security.Cryptography.MD5.HashData(input);
        return new Guid(hash);
    }

    private static string FormatThinkingDisplay(ChatThinkingBlock block)
    {
        if (block.IsThinking)
        {
            return block.Thinking ?? string.Empty;
        }

        if (block.IsRedactedThinking)
        {
            return "Thinking (redacted)";
        }

        return string.Empty;
    }

    private static IReadOnlyList<ChatThinkingBlock>? DeserializeThinkingBlocks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var blocks = JsonSerializer.Deserialize<List<ChatThinkingBlock>>(json, _jsonOptions);
            return blocks is { Count: > 0 } ? blocks : null;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to deserialize thinking blocks: {ex.Message}");
            return null;
        }
    }



    /// <summary>
    /// Creates multiple OpenAI messages from a single notebook file attachment.
    /// </summary>
    private async Task<List<ChatMessage>> CreateOpenAiMessagesFromNotebookFileAsync(Guid notebookFileId, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>();
        if (_notebookFileService == null) return messages;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var notebookFile = await db.NotebookFiles
                .Include(nf => nf.Notebook)
                .FirstOrDefaultAsync(nf => nf.Id == notebookFileId, cancellationToken);
            if (notebookFile == null) return messages;

            return await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
                notebookFile,
                _notebookFileService,
                _markdownExtractionService,
                _storagePath,
                cancellationToken,
                _markdownAttachmentOptions.Value.MaxInlineCharacters,
                _logger);
        }
        catch (GuideAntsApi.Exceptions.AttachmentNotReadyException)
        {
            // Let this exception propagate to be sent as an error event to the client
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating OpenAI messages for file {NotebookFileId}", notebookFileId);
            return messages;
        }
    }


    /// <summary>
    /// NEW: Adds multiple attachments to a user message during send operation
    /// </summary>
    private async Task AddAttachmentsToUserMessageAsync(Guid userMessageId, Guid notebookId, IReadOnlyList<AttachmentDto> attachments, CancellationToken cancellationToken = default)
    {
        if (attachments == null || attachments.Count == 0) return;

        // Create a new scope to get a fresh DbContext instance to avoid concurrency issues
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        for (int i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];

            // Verify file exists and belongs to notebook
            var notebookFile = await db.NotebookFiles
                .FirstOrDefaultAsync(f => f.Id == attachment.NotebookFileId && f.NotebookId == notebookId, cancellationToken);

            if (notebookFile == null)
            {
                _logger.LogWarning("Attachment file {NotebookFileId} not found or doesn't belong to notebook {NotebookId}",
                    attachment.NotebookFileId, notebookId);
                continue;
            }

            var messageAttachment = new MessageAttachment
            {
                MessageId = userMessageId,
                NotebookFileId = attachment.NotebookFileId,
                Type = AttachmentType.Referenced,
                OrderIndex = i,
                Created = DateTime.UtcNow
            };

            db.MessageAttachments.Add(messageAttachment);
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Added {Count} attachments to message {MessageId}",
            attachments.Count, userMessageId);
    }

    /// <summary>
    /// Creates multiple OpenAI Content items from a notebook file.
    /// </summary>
    private async Task<List<ChatContent>> CreateOpenAiContentFromNotebookFileAsync(Guid notebookFileId, CancellationToken cancellationToken = default)
    {
        var contents = new List<ChatContent>();
        if (_notebookFileService == null) return contents;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var notebookFile = await db.NotebookFiles
                .Include(nf => nf.Notebook)
                .FirstOrDefaultAsync(nf => nf.Id == notebookFileId, cancellationToken);
            if (notebookFile == null) return contents;

            return await AttachmentMessageBuilder.CreateContentFromNotebookFileAsync(
                notebookFile,
                _notebookFileService,
                _markdownExtractionService,
                _storagePath,
                cancellationToken,
                _markdownAttachmentOptions.Value.MaxInlineCharacters,
                _logger);
        }
        catch (GuideAntsApi.Exceptions.AttachmentNotReadyException)
        {
            // Let this exception propagate to be sent as an error event to the client
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating OpenAI content for file {NotebookFileId}", notebookFileId);
            return contents;
        }
    }

    /// <summary>
    /// NEW: Determines file type string from file name for DTO
    /// </summary>
    private static string DetermineFileTypeString(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "tiff" or "webp" => "image",
            "wav" or "mp3" or "flac" or "aac" or "ogg" or "m4a" => "audio",
            "txt" or "md" or "json" or "xml" or "csv" => "text",
            _ => "other"
        };
    }

    #endregion

    #region Streaming helpers (extracted for readability)

    private sealed class StreamSendContext
    {
        public required Guid ConversationId { get; init; }
        public required NotebookConversation Conversation { get; init; }
        public required SendMessageRequest Request { get; init; }
        public required User DbUser { get; init; }
        public required string AssistantName { get; init; }
        public string? ModelDeploymentId { get; init; }
        public ResolvedExecutionPolicy? ExecutionPolicy { get; init; }
        public required List<ChatMessage> PreviousMessages { get; init; }
        public Guid? AssistantId { get; init; }

        // populated later
        public int TurnIndex { get; set; }
        public ConversationTurn? DbTurn { get; set; }
        public NotebookConversationMessage? UserMessage { get; set; }
        public bool TurnReportedFileChanges { get; set; }
    }

    private sealed class StreamingInfra
    {
        public required Channel<StreamingEvent> Channel { get; init; }
        public required ChannelWriter<StreamingEvent> Writer { get; init; }
        public required SemaphoreSlim Throttler { get; init; }
        public required MessageAddedEventHandler OnMessageAdded { get; init; }
        public required StreamingMessageProgressEventHandler OnStreamingProgress { get; init; }
    }

    private async Task<StreamSendContext> LoadStreamContextAsync(Guid conversationId, SendMessageRequest request, CancellationToken ct)
    {
        // Allow empty instructions if there are attachments (attachments provide the context)
        if (string.IsNullOrWhiteSpace(request.Instructions) && (request.Attachments == null || request.Attachments.Count == 0))
            throw new ArgumentException("Instructions required", nameof(request));

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conv = await db.NotebookConversations
            .Include(c => c.Messages)
                .ThenInclude(m => m.EditHistory)
            .Include(c => c.Notebook)
                .ThenInclude(n => n.Guide)
            .Include(c => c.Notebook)
                .ThenInclude(n => n.Project)
            .Include(c => c.Turns) // Include Turns upfront to avoid loading them later
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new KeyNotFoundException("Conversation not found");


        var dbUser = new User { Id = Guid.Empty, Name = "User", Email = "user@example.com" };

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
            conversationId,
            assistantName,
            string.IsNullOrWhiteSpace(requestedModelDeploymentId) ? "(unset)" : requestedModelDeploymentId,
            resolvedModel.ModelId,
            resolvedModel.ReferenceKind,
            resolvedModel.ExecutionPolicy.Authority,
            string.Join(", ", resolvedModel.ExecutionPolicy.Parameters.Keys));

        var previousMessages = await PrepareMessagesForAssistantAsync(conv, assistantName, dbUser.Id, ct);

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
            DbUser = dbUser,
            AssistantName = assistantName,
            ModelDeploymentId = modelDeploymentId,
            ExecutionPolicy = resolvedModel.ExecutionPolicy,
            PreviousMessages = previousMessages,
            AssistantId = assistantId
        };
    }

    private async Task CreateTurnAndUserMessageAsync(StreamSendContext ctx, CancellationToken ct)
    {
        // Create a new scope to get a fresh DbContext instance to avoid concurrency issues
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Determine next turn index
        var turnIndex = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == ctx.ConversationId)
            .MaxAsync(t => (int?)t.TurnIndex, ct) ?? 0;
        turnIndex++;

        var dbTurn = new ConversationTurn
        {
            NotebookConversationId = ctx.ConversationId,
            TurnIndex = turnIndex,
            AssistantName = ctx.AssistantName,
            ModelDeploymentId = ctx.ModelDeploymentId,
            Instructions = ctx.Request.Instructions,
            Created = DateTime.UtcNow
        };

        db.ConversationTurns.Add(dbTurn);
        await db.SaveChangesAsync(ct);

        // System message is now always added in PrepareMessagesForAssistantAsync

        // Add user message
        var userMessage = new NotebookConversationMessage
        {
            NotebookConversationId = ctx.Conversation.Id,
            TurnIndex = turnIndex,
            MessageSequence = 1,
            Role = DataModelChatRole.User,
            Content = ctx.Request.Instructions,
            AssistantName = "user",
            ModelDeploymentId = ctx.ModelDeploymentId,
            UserId = null,
            Created = DateTime.UtcNow,
            AssistantId = ctx.AssistantId
        };
        db.NotebookConversationMessages.Add(userMessage);
        await db.SaveChangesAsync(ct);

        // store
        ctx.TurnIndex = turnIndex;
        ctx.DbTurn = dbTurn;
        ctx.UserMessage = userMessage;
    }

    private async Task ProcessAttachmentsAsync(StreamSendContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Attachments == null || ctx.Request.Attachments.Count == 0) return;

        await AddAttachmentsToUserMessageAsync(ctx.UserMessage!.Id, ctx.Conversation.NotebookId, ctx.Request.Attachments, ct);

        foreach (var attachment in ctx.Request.Attachments)
        {
            var messages = await CreateOpenAiMessagesFromNotebookFileAsync(attachment.NotebookFileId, ct);
            foreach (var message in messages)
            {
                ctx.PreviousMessages.Add(message);
            }
        }
    }

    private ChatRunOptions BuildChatRunOptions(StreamSendContext ctx)
    {
        return new ChatRunOptions
        {
            AssistantName = ctx.AssistantName,
            DeploymentId = ctx.ModelDeploymentId,
            Instructions = ctx.Request.Instructions,
            oAuthUserAccessToken = ctx.Request.ExternalAuthTokens?.FirstOrDefault().Value,
            ExternalAuthTokens = ctx.Request.ExternalAuthTokens,
            ExecutionPolicy = ctx.ExecutionPolicy,
        };
    }

    private StreamingInfra InitStreamingInfrastructure(StreamSendContext ctx, CancellationToken ct)
    {
        var channelOptions = new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        var channel = Channel.CreateBounded<StreamingEvent>(channelOptions);
        var writer = channel.Writer;
        var throttler = new SemaphoreSlim(50, 50);

        // Handlers
        StreamingMessageProgressEventHandler onProgress = (_, e) =>
        {
            if (ct.IsCancellationRequested) return;

            var payload = new { role = "assistant", contentDelta = e.ContentDelta, timestamp = DateTime.UtcNow };
            var ev = new StreamingEvent("assistant_message", JsonSerializer.Serialize(payload, _jsonOptions));

            if (throttler.Wait(100))
            {
                try { if (!ct.IsCancellationRequested) writer.TryWrite(ev); }
                finally { throttler.Release(); }
            }
        };

        MessageAddedEventHandler onMessageAdded = (_, e) =>
        {
            if (ct.IsCancellationRequested) return;
            if (string.IsNullOrEmpty(e.Role) || string.IsNullOrEmpty(e.Message)) return;

            if (e.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                var toolPayload = new { role = e.Role.ToLowerInvariant(), content = e.Message, toolCallId = e.ToolCallId, functionName = e.FunctionName, arguments = e.ToolCallsJson, timestamp = DateTime.UtcNow };
                var toolEv = new StreamingEvent("tool_result", JsonSerializer.Serialize(toolPayload, _jsonOptions));
                if (throttler.Wait(100)) { try { writer.TryWrite(toolEv); } finally { throttler.Release(); } }
                return;
            }

            var eventType = DetermineEventType(e.Role, e.Message);
            var payload = new { role = e.Role.ToLowerInvariant(), content = e.Message, timestamp = DateTime.UtcNow };
            var ev = new StreamingEvent(eventType, JsonSerializer.Serialize(payload, _jsonOptions));
            if (throttler.Wait(100)) { try { writer.TryWrite(ev); } finally { throttler.Release(); } }
        };

        return new StreamingInfra { Channel = channel, Writer = writer, Throttler = throttler, OnMessageAdded = onMessageAdded, OnStreamingProgress = onProgress };
    }

    private void StartChatRunnerBackgroundTask(StreamSendContext ctx, ChatRunOptions chatOptions, StreamingInfra infra, CancellationToken externalCt, Guid conversationId)
    {
        // local uncancellable token for DB writes
        var noneCt = CancellationToken.None;

        _ = Task.Run(async () =>
        {
            Guid? currentAssistantMessageId = null;
            var currentAssistantContent = new StringBuilder();
            var currentMessageSequence = 2; // Start from 2 since user message is sequence 1
            // Map of filename -> correct absolute URL discovered from tool outputs in this turn
            var fileUrlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assistantMessageIds = new List<Guid>();
            var thinkingEmittedInStream = false;

            int flushCounter = 0;
            const int FLUSH_INTERVAL = 20;

            // Wrap streaming progress to both emit tokens and persist
            StreamingMessageProgressEventHandler progressHandler = (_, e) =>
            {
                if (externalCt.IsCancellationRequested) return;

                if (string.Equals(e.Role, "assistant_thinking", StringComparison.OrdinalIgnoreCase))
                {
                    thinkingEmittedInStream = true;
                }

                // If we don't have a current assistant message, create one
                if (currentAssistantMessageId == null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var msg = new NotebookConversationMessage
                    {
                        NotebookConversationId = ctx.Conversation.Id,
                        TurnIndex = ctx.TurnIndex,
                        MessageSequence = currentMessageSequence++,
                        Role = DataModelChatRole.Assistant,
                        AssistantName = ctx.AssistantName,
                        ModelDeploymentId = ctx.ModelDeploymentId,
                        Content = string.Empty,
                        IsStreaming = true,
                        Created = DateTime.UtcNow,
                        AssistantId = ctx.AssistantId
                    };
                    db.NotebookConversationMessages.Add(msg);

                    // Update turn's LastUpdated for polling detection
                    var turn = db.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                    turn.LastUpdated = DateTime.UtcNow;

                    db.SaveChanges();
                    currentAssistantMessageId = msg.Id;
                    assistantMessageIds.Add(msg.Id);
                }

                currentAssistantContent.Append(e.ContentDelta);
                flushCounter++;

                if (flushCounter % FLUSH_INTERVAL == 0)
                {
                    try
                    {
                        if (currentAssistantMessageId != null)
                        {
                            using var scope2 = _scopeFactory.CreateScope();
                            var db2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            var stub = new NotebookConversationMessage { Id = currentAssistantMessageId.Value };
                            db2.Attach(stub);
                            stub.Content = currentAssistantContent.ToString();
                            db2.Entry(stub).Property(x => x.Content).IsModified = true;

                            // Update turn's LastUpdated for polling detection
                            var turn = db2.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                            turn.LastUpdated = DateTime.UtcNow;

                            db2.SaveChanges();
                        }

                        // Broadcast periodic progress to observers (less frequent than token events)
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Only broadcast if we can verify user identity
                                var activeUserName = "User";
                                if (string.IsNullOrWhiteSpace(activeUserName))
                                {
                                    _logger.LogError("Cannot broadcast streaming progress - user identity not established");
                                    return;
                                }

                                await _broadcastHub.BroadcastToConversationAsync(conversationId,
                                    new StreamingEvent(StreamingEventTypes.StreamingProgress, JsonSerializer.Serialize(new
                                    {
                                        userId = Guid.Empty,
                                        activeUserName = activeUserName,
                                        contentLength = currentAssistantContent.Length,
                                        tokensProcessed = flushCounter,
                                        timestamp = DateTime.UtcNow
                                    }, _jsonOptions)));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to broadcast streaming progress");
                            }
                        });
                    }
                    catch { /* log later */ }
                }

                infra.OnStreamingProgress(_, e); // still writes to channel
            };

            // Wrap message added to handle assistant messages with tool calls and tool messages
            MessageAddedEventHandler messageAddedHandler = (_, e) =>
            {
                if (externalCt.IsCancellationRequested) return;

                // Handle assistant messages that might have tool calls
                if (e.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(e.ToolCallsJson))
                    {
                        // TOOL CALL MESSAGE: Update existing streaming message or create new one
                        var hostUrl = _configuration?["ANTRUNNER_SERVICES_HOST_URL"] ?? Environment.GetEnvironmentVariable("ANTRUNNER_SERVICES_HOST_URL");
                        var toolCallAssistantText = SanitizeAssistantContent(e.Message ?? string.Empty, fileUrlMap, hostUrl);

                        var toolCallsJson = e.ToolCallsJson;
                        List<ChatToolCall>? toolCallsForDb = null;
                        try
                        {
                            toolCallsForDb = JsonSerializer.Deserialize<List<ChatToolCall>>(toolCallsJson!, _jsonOptions);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Failed to deserialize tool calls for conversation {ConversationId} turn {TurnIndex}",
                                ctx.Conversation.Id,
                                ctx.TurnIndex);
                        }

                        if (currentAssistantMessageId != null)
                        {
                            // UPDATE existing streaming message with ToolCalls (avoids duplicate rows)
                            using var scopeUpdate = _scopeFactory.CreateScope();
                            var dbUpdate = scopeUpdate.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            var stub = new NotebookConversationMessage { Id = currentAssistantMessageId.Value };
                            dbUpdate.Attach(stub);
                            stub.Content = toolCallAssistantText;
                            stub.ToolCalls = toolCallsJson;
                            stub.IsStreaming = false;
                            dbUpdate.Entry(stub).Property(x => x.Content).IsModified = true;
                            dbUpdate.Entry(stub).Property(x => x.ToolCalls).IsModified = true;
                            dbUpdate.Entry(stub).Property(x => x.IsStreaming).IsModified = true;

                            // Update turn's LastUpdated for polling detection
                            var turn = dbUpdate.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                            turn.LastUpdated = DateTime.UtcNow;

                            dbUpdate.SaveChanges();
                        }
                        else
                        {
                            // No streaming message exists, create new one
                            var toolCallMessage = new NotebookConversationMessage
                            {
                                NotebookConversationId = ctx.Conversation.Id,
                                TurnIndex = ctx.TurnIndex,
                                MessageSequence = currentMessageSequence++,
                                Role = DataModelChatRole.Assistant,
                                AssistantName = ctx.AssistantName,
                                ModelDeploymentId = ctx.ModelDeploymentId,
                                Content = toolCallAssistantText,
                                ToolCalls = toolCallsJson,
                                IsStreaming = false,
                                Created = DateTime.UtcNow,
                                AssistantId = ctx.AssistantId
                            };

                            using var scopeCreate = _scopeFactory.CreateScope();
                            var dbCreate = scopeCreate.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            dbCreate.NotebookConversationMessages.Add(toolCallMessage);

                            // Update turn's LastUpdated for polling detection
                            var turn = dbCreate.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                            turn.LastUpdated = DateTime.UtcNow;

                            dbCreate.SaveChanges();
                            assistantMessageIds.Add(toolCallMessage.Id);
                        }

                        // Tool call usage is recorded in finalization after stream completes
                        // so all message IDs are available and we have complete data.

                        // Emit assistant_message containing tool_calls so the client can render the call immediately
                        try
                        {
                            // Normalize tool calls for client consumption (id, function.name, function.arguments)
                            var toolCallsForClient = toolCallsForDb?.Select(tc => new
                            {
                                id = tc.Id,
                                type = tc.Type.ToString().ToLowerInvariant(),
                                function = new
                                {
                                    name = tc.Function.Name,
                                    arguments = tc.Function.Arguments.ToString()
                                }
                            }).ToList();

                            var assistantToolCallPayload = new
                            {
                                role = "assistant",
                                content = string.Empty,
                                tool_calls = toolCallsForClient,
                                timestamp = DateTime.UtcNow
                            };
                            var assistantToolCallEvent = new StreamingEvent(
                                "assistant_message",
                                JsonSerializer.Serialize(assistantToolCallPayload, _jsonOptions)
                            );
                            infra.Writer.TryWrite(assistantToolCallEvent);
                        }
                        catch { /* non-fatal if we cannot emit */ }

                        // Don't set currentAssistantMessageId - this message is complete
                        currentAssistantMessageId = null;
                        currentAssistantContent.Clear();
                    }
                    else if (currentAssistantMessageId == null)
                    {
                        // CONTENT MESSAGE WITHOUT STREAMING: Create new message
                        var hostUrl = _configuration?["ANTRUNNER_SERVICES_HOST_URL"] ?? Environment.GetEnvironmentVariable("ANTRUNNER_SERVICES_HOST_URL");
                        var sanitized = SanitizeAssistantContent(e.Message ?? string.Empty, fileUrlMap, hostUrl);
                        var contentMessage = new NotebookConversationMessage
                        {
                            NotebookConversationId = ctx.Conversation.Id,
                            TurnIndex = ctx.TurnIndex,
                            MessageSequence = currentMessageSequence++,
                            Role = DataModelChatRole.Assistant,
                            AssistantName = ctx.AssistantName,
                            ModelDeploymentId = ctx.ModelDeploymentId,
                            Content = sanitized,
                            IsStreaming = false,
                            Created = DateTime.UtcNow,
                            AssistantId = ctx.AssistantId
                        };

                        using (var scopeContent = _scopeFactory.CreateScope())
                        {
                            var dbContent = scopeContent.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            dbContent.NotebookConversationMessages.Add(contentMessage);

                            // Update turn's LastUpdated for polling detection
                            var turn = dbContent.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                            turn.LastUpdated = DateTime.UtcNow;

                            dbContent.SaveChanges();
                        }

                        assistantMessageIds.Add(contentMessage.Id);

                        // Broadcast sanitized content instead of original
                        infra.OnMessageAdded(_, new MessageAddedEventArgs("assistant", sanitized));
                        return;
                    }
                    else
                    {
                        // STREAMING MESSAGE: Update existing streaming message
                        var hostUrl = _configuration?["ANTRUNNER_SERVICES_HOST_URL"] ?? Environment.GetEnvironmentVariable("ANTRUNNER_SERVICES_HOST_URL");
                        var sanitized = SanitizeAssistantContent(e.Message ?? string.Empty, fileUrlMap, hostUrl);

                        using (var scopeUpdate = _scopeFactory.CreateScope())
                        {
                            var dbUpdate = scopeUpdate.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            var stubUpdate = new NotebookConversationMessage { Id = currentAssistantMessageId.Value };
                            dbUpdate.Attach(stubUpdate);

                            stubUpdate.Content = sanitized;
                            stubUpdate.IsStreaming = false;

                            dbUpdate.Entry(stubUpdate).Property(x => x.Content).IsModified = true;
                            dbUpdate.Entry(stubUpdate).Property(x => x.IsStreaming).IsModified = true;

                            if (!string.IsNullOrEmpty(e.ToolCallsJson))
                            {
                                stubUpdate.ToolCalls = e.ToolCallsJson;
                                dbUpdate.Entry(stubUpdate).Property(x => x.ToolCalls).IsModified = true;
                            }

                            // Update turn's LastUpdated for polling detection
                            var turn = dbUpdate.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                            turn.LastUpdated = DateTime.UtcNow;

                            // Persist
                            dbUpdate.SaveChanges();
                        }

                        currentAssistantMessageId = null;
                        currentAssistantContent.Clear();
                        infra.OnMessageAdded(_, new MessageAddedEventArgs("assistant", sanitized));
                        return;
                    }

                    // Clear the tracker so that any subsequent assistant replies are saved as NEW messages
                    currentAssistantMessageId = null;
                    currentAssistantContent.Clear();

                    // For tool-call-only assistant messages, broadcast the original event
                    infra.OnMessageAdded(_, e);
                    return;
                }
                else if (e.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    // Sanitize tool output to ensure no sandbox: URLs are persisted or broadcast
                    var sanitizedContent = ConvertSandboxUrlsToRelative(e.Message ?? string.Empty);

                    // Create tool message immediately
                    try
                    {
                        var toolMessage = new NotebookConversationMessage
                        {
                            NotebookConversationId = ctx.Conversation.Id,
                            TurnIndex = ctx.TurnIndex,
                            MessageSequence = currentMessageSequence++,
                            Role = DataModelChatRole.Tool,
                            Content = sanitizedContent,
                            ToolCallId = e.ToolCallId,
                            FunctionName = e.FunctionName,
                            IsStreaming = false,
                            Created = DateTime.UtcNow,
                            AssistantId = ctx.AssistantId,
                            AssistantName = ctx.AssistantName
                        };
                        using (var scopeTool = _scopeFactory.CreateScope())
                        {
                            var dbTool = scopeTool.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            dbTool.NotebookConversationMessages.Add(toolMessage);

                            // Update turn's LastUpdated for polling detection
                            var turn = dbTool.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                            turn.LastUpdated = DateTime.UtcNow;

                            dbTool.SaveChanges();
                        }

                        // Tool call usage is recorded in finalization after stream completes.

                        // Update filename→URL map from this tool output for later assistant sanitization
                        try
                        {
                            var projectId = ctx.Conversation.Notebook.ProjectId;
                            var notebookId = ctx.Conversation.NotebookId;
                            var hostUrl = _configuration?["ANTRUNNER_SERVICES_HOST_URL"] ?? Environment.GetEnvironmentVariable("ANTRUNNER_SERVICES_HOST_URL");
                            // Use sanitized content for map extraction as well
                            foreach (var kv in ExtractFilenameUrlMapFromToolMessage(sanitizedContent, projectId, notebookId, hostUrl))
                            {
                                fileUrlMap[kv.Key] = kv.Value;
                            }
                        }
                        catch { /* non-fatal */ }
                    }
                    catch { /* log later */ }

                    // Broadcast sanitized message
                    var sanitizedArgs = new MessageAddedEventArgs(e.Role, sanitizedContent, e.ToolCallId, e.FunctionName, e.ToolCallsJson);
                    infra.OnMessageAdded(_, sanitizedArgs);
                    return;
                }

                infra.OnMessageAdded(_, e); // still writes to channel
            };

            try
            {
                externalCt.ThrowIfCancellationRequested();

                var httpClient = _httpClientFactory.CreateClient();
                var output = await ChatRunner.RunThread(chatOptions, _chatClientFactory,
                    previousMessages: ctx.PreviousMessages,
                    httpClient: httpClient,
                    messageAdded: messageAddedHandler,
                    streamingMessageProgress: progressHandler,
                    projectId: ctx.Conversation.Notebook.ProjectId.ToString(),
                    notebookId: ctx.Conversation.NotebookId.ToString(),
                    conversationId: ctx.Conversation.Id.ToString(),
                    turnIndex: ctx.TurnIndex,
                    assistantId: ctx.AssistantId,
                    notebookConversationMessageId: ctx.UserMessage?.Id,
                    cancellationToken: externalCt);

                // Finalize any remaining streaming message (messageAddedHandler handles complete messages)
                if (currentAssistantMessageId != null)
                {
                    using var scopeFinal = _scopeFactory.CreateScope();
                    var dbFinal = scopeFinal.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // Check if it's still streaming
                    var existingMsg = await dbFinal.NotebookConversationMessages
                        .Where(m => m.Id == currentAssistantMessageId.Value)
                        .Select(m => new { m.IsStreaming })
                        .FirstOrDefaultAsync(noneCt);

                    if (existingMsg != null && (existingMsg.IsStreaming ?? false))
                    {
                        // Update to finalize
                        var stubFinal = new NotebookConversationMessage { Id = currentAssistantMessageId.Value };
                        dbFinal.Attach(stubFinal);
                        stubFinal.Content = currentAssistantContent.ToString();
                        stubFinal.IsStreaming = false;
                        dbFinal.Entry(stubFinal).Property(x => x.Content).IsModified = true;
                        dbFinal.Entry(stubFinal).Property(x => x.IsStreaming).IsModified = true;

                        // Update turn's LastUpdated for polling detection
                        var turnFinal = dbFinal.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                        turnFinal.LastUpdated = DateTime.UtcNow;

                        await dbFinal.SaveChangesAsync(noneCt);
                    }
                }

                await PersistThinkingBlocksAsync(output, assistantMessageIds, noneCt);
                if (!thinkingEmittedInStream)
                {
                    EmitThinkingMessages(output, assistantMessageIds, infra.Writer);
                }

                await RecordToolCallUsageForTurnAsync(ctx, noneCt);

                // Update turn with completion details
                if (output != null)
                {
                    ctx.TurnReportedFileChanges =
                        (output.NewFiles?.Count > 0) ||
                        (output.ModifiedFiles?.Count > 0);

                    using (var turnScope = _scopeFactory.CreateScope())
                    {
                        var turnDb = turnScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        turnDb.Attach(ctx.DbTurn!);
                        ctx.DbTurn!.ChatRunOutputJson = JsonSerializer.Serialize(output, _jsonOptions);
                        ctx.DbTurn!.UsageJson = output.Usage != null ? JsonSerializer.Serialize(output.Usage, _jsonOptions) : null;

                        // Store file changes tracked during tool execution
                        if (output.NewFiles != null && output.NewFiles.Count > 0)
                        {
                            ctx.DbTurn!.FilesCreated = JsonSerializer.Serialize(output.NewFiles, _jsonOptions);
                        }
                        if (output.ModifiedFiles != null && output.ModifiedFiles.Count > 0)
                        {
                            ctx.DbTurn!.FilesModified = JsonSerializer.Serialize(output.ModifiedFiles, _jsonOptions);
                        }

                        await turnDb.SaveChangesAsync(noneCt);
                    }

                    if (output.Usage != null)
                    {
                        var usagePayload = new { promptTokens = output.Usage.PromptTokens, completionTokens = output.Usage.CompletionTokens, totalTokens = output.Usage.TotalTokens };
                        infra.Writer.TryWrite(new StreamingEvent("usage", JsonSerializer.Serialize(usagePayload, _jsonOptions)));

                        try
                        {
                            var cached = output.Usage.CachedPromptTokens ?? 0;
                            var prompt = output.Usage.PromptTokens ?? 0;
                            var completion = output.Usage.CompletionTokens ?? 0;
                            var reasoning = 0; // Not exposed by current UsageResponse

                            var metrics = new UsageMetrics(
                                ValueInput: prompt,
                                ValueCachedInput: cached,
                                ValueReasoning: reasoning,
                                ValueOutput: completion);
                            var usageService = LlmProviderResolver.ResolveUsageServiceName(ctx.ModelDeploymentId, _scopeFactory);

                            // currentAssistantMessageId is cleared when messageAddedHandler finalizes assistant rows;
                            // usage still needs NotebookConversationMessageId for conversational attribution.
                            Guid? messageIdForUsage = currentAssistantMessageId
                                ?? (assistantMessageIds.Count > 0 ? assistantMessageIds[^1] : null);
                            if (messageIdForUsage == null)
                            {
                                using var scopeUsage = _scopeFactory.CreateScope();
                                var dbUsage = scopeUsage.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                                messageIdForUsage = await dbUsage.NotebookConversationMessages
                                    .Where(m => m.NotebookConversationId == ctx.Conversation.Id
                                             && m.TurnIndex == ctx.TurnIndex
                                             && m.Role == DataModelChatRole.Assistant)
                                    .OrderByDescending(m => m.Created)
                                    .Select(m => (Guid?)m.Id)
                                    .FirstOrDefaultAsync(noneCt);
                            }

                            await _usageRecorder.RecordChatAsync(
                                projectId: ctx.Conversation.Notebook.ProjectId,
                                notebookId: ctx.Conversation.NotebookId,
                                service: usageService,
                                modelDeploymentId: ctx.ModelDeploymentId ?? string.Empty,
                                metrics: metrics,
                                conversationId: ctx.Conversation.Id,
                                assistantId: ctx.AssistantId,
                                notebookConversationMessageId: messageIdForUsage);
                        }
                        catch { /* non-fatal usage logging */ }
                    }
                }

            }
            catch (OperationCanceledException)
            {
                // Finalize any partial assistant message on cancellation (make it visible and complete)
                if (currentAssistantMessageId != null)
                {
                    using var scopeCancel = _scopeFactory.CreateScope();
                    var dbCancel = scopeCancel.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var stubCancel = new NotebookConversationMessage { Id = currentAssistantMessageId.Value };
                    dbCancel.Attach(stubCancel);
                    stubCancel.Content = currentAssistantContent.ToString();
                    stubCancel.IsStreaming = false;
                    dbCancel.Entry(stubCancel).Property(x => x.Content).IsModified = true;
                    dbCancel.Entry(stubCancel).Property(x => x.IsStreaming).IsModified = true;

                    // Update turn's LastUpdated for polling detection
                    var turnCancel = dbCancel.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                    turnCancel.LastUpdated = DateTime.UtcNow;

                    await dbCancel.SaveChangesAsync(noneCt);
                }

                // Prune any incomplete tool calls that did not produce tool results for this turn
                try
                {
                    await PruneIncompleteToolCallsAsync(ctx.Conversation.Id, ctx.TurnIndex, CancellationToken.None);
                }
                catch (Exception pruneEx)
                {
                    _logger.LogWarning(pruneEx, "Failed to prune incomplete tool calls for conversation {ConversationId} turn {TurnIndex}", ctx.Conversation.Id, ctx.TurnIndex);
                }

                // Persist usage even for cancelled turns so guide usage/invocation reports remain complete.
                await RecordToolCallUsageForTurnAsync(ctx, noneCt);
                await RecordCancelledTurnMarkerUsageAsync(
                    ctx,
                    currentAssistantMessageId,
                    assistantMessageIds,
                    noneCt);

                var cancelPayload = new { message = "Stream was cancelled by user", type = "Cancellation", timestamp = DateTime.UtcNow, turnIndex = ctx.TurnIndex };
                infra.Writer.TryWrite(new StreamingEvent("cancelled", JsonSerializer.Serialize(cancelPayload, _jsonOptions)));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Streaming conversation failed for {ConversationId} turn {TurnIndex}",
                    ctx.Conversation.Id,
                    ctx.TurnIndex);

                // Finalize any partial assistant message on error (make it visible and complete)
                if (currentAssistantMessageId != null)
                {
                    using var scopeErr = _scopeFactory.CreateScope();
                    var dbErr = scopeErr.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var stubErr = new NotebookConversationMessage { Id = currentAssistantMessageId.Value };
                    dbErr.Attach(stubErr);
                    stubErr.Content = currentAssistantContent.ToString();
                    stubErr.IsStreaming = false;
                    dbErr.Entry(stubErr).Property(x => x.Content).IsModified = true;
                    dbErr.Entry(stubErr).Property(x => x.IsStreaming).IsModified = true;

                    // Update turn's LastUpdated for polling detection
                    var turnErr = dbErr.ConversationTurns.First(t => t.Id == ctx.DbTurn!.Id);
                    turnErr.LastUpdated = DateTime.UtcNow;

                    await dbErr.SaveChangesAsync(noneCt);
                }

                var err = StreamingErrorEnvelope.Build(ex);
                infra.Writer.TryWrite(new StreamingEvent("error", JsonSerializer.Serialize(err, _jsonOptions)));
            }
            finally
            {
                infra.Throttler.Dispose();
                infra.Writer.Complete();
            }
        }, externalCt);
    }

    private async Task RecordToolCallUsageForTurnAsync(StreamSendContext ctx, CancellationToken ct)
    {
        try
        {
            using var scopeUsage = _scopeFactory.CreateScope();
            var dbUsage = scopeUsage.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Get all tool messages for this turn.
            var toolMessages = await dbUsage.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == ctx.Conversation.Id
                         && m.TurnIndex == ctx.TurnIndex
                         && m.Role == DataModelChatRole.Tool
                         && m.FunctionName != null)
                .Select(m => new { m.Id, m.FunctionName, m.ToolCallId, ContentLength = m.Content != null ? m.Content.Length : 0 })
                .ToListAsync(ct);

            if (toolMessages.Count == 0)
            {
                return;
            }

            // Prevent duplicate usage rows if this is called multiple times (e.g., cancellation + retries).
            var toolMessageIds = toolMessages.Select(m => m.Id).ToList();
            var alreadyRecordedIds = await dbUsage.UsageEvents
                .Where(u => u.NotebookConversationMessageId != null
                         && toolMessageIds.Contains(u.NotebookConversationMessageId.Value)
                         && u.Category == GuideAntsApi.DataModel.Models.UsageCategory.ToolCall)
                .Select(u => u.NotebookConversationMessageId!.Value)
                .ToListAsync(ct);
            var alreadyRecordedSet = new HashSet<Guid>(alreadyRecordedIds);

            foreach (var toolMsg in toolMessages)
            {
                // Skip crew bridge invocations - they record their own usage.
                if (string.Equals(toolMsg.FunctionName, "InvokeAgent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (alreadyRecordedSet.Contains(toolMsg.Id))
                {
                    continue;
                }

                await _usageRecorder.RecordToolCallAsync(
                    projectId: ctx.Conversation.Notebook.ProjectId,
                    notebookId: ctx.Conversation.NotebookId,
                    conversationId: ctx.Conversation.Id,
                    functionName: toolMsg.FunctionName!,
                    metadataJson: JsonSerializer.Serialize(new
                    {
                        toolCallId = toolMsg.ToolCallId,
                        functionName = toolMsg.FunctionName,
                        contentLength = toolMsg.ContentLength
                    }),
                    assistantId: ctx.AssistantId,
                    notebookConversationMessageId: toolMsg.Id,
                    ct: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record tool call usage for turn {TurnIndex}", ctx.TurnIndex);
        }
    }

    private async Task RecordCancelledTurnMarkerUsageAsync(
        StreamSendContext ctx,
        Guid? currentAssistantMessageId,
        IReadOnlyList<Guid> assistantMessageIds,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var turnMessageIds = await db.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == ctx.Conversation.Id
                         && m.TurnIndex == ctx.TurnIndex)
                .Select(m => m.Id)
                .ToListAsync(ct);

            if (turnMessageIds.Count == 0)
            {
                return;
            }

            // If any usage already exists for this turn, we don't need a synthetic marker.
            var hasUsageForTurn = await db.UsageEvents
                .Where(u => u.NotebookConversationMessageId != null
                         && turnMessageIds.Contains(u.NotebookConversationMessageId.Value))
                .AnyAsync(ct);

            if (hasUsageForTurn)
            {
                return;
            }

            Guid? messageIdForUsage = currentAssistantMessageId
                ?? (assistantMessageIds.Count > 0 ? assistantMessageIds[^1] : null);

            if (messageIdForUsage == null)
            {
                messageIdForUsage = await db.NotebookConversationMessages
                    .Where(m => m.NotebookConversationId == ctx.Conversation.Id
                             && m.TurnIndex == ctx.TurnIndex
                             && m.Role == DataModelChatRole.Assistant)
                    .OrderByDescending(m => m.Created)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefaultAsync(ct);
            }

            if (messageIdForUsage == null)
            {
                return;
            }

            var usageService = LlmProviderResolver.ResolveUsageServiceName(ctx.ModelDeploymentId, _scopeFactory);
            var markerMetadata = JsonSerializer.Serialize(new
            {
                cancellationType = "user_cancelled",
                turnIndex = ctx.TurnIndex
            });

            await _usageRecorder.RecordChatAsync(
                projectId: ctx.Conversation.Notebook.ProjectId,
                notebookId: ctx.Conversation.NotebookId,
                service: usageService,
                modelDeploymentId: ctx.ModelDeploymentId ?? string.Empty,
                metrics: new UsageMetrics(ValueInput: 0, ValueCachedInput: 0, ValueReasoning: 0, ValueOutput: 0),
                conversationId: ctx.Conversation.Id,
                metadataJson: markerMetadata,
                assistantId: ctx.AssistantId,
                notebookConversationMessageId: messageIdForUsage,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to record cancelled-turn usage marker for conversation {ConversationId} turn {TurnIndex}",
                ctx.Conversation.Id,
                ctx.TurnIndex);
        }
    }

    #endregion

    // Helper: prune incomplete tool calls after cancellation for a given turn
    private async Task PruneIncompleteToolCallsAsync(Guid conversationId, int turnIndex, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Load all messages for the target turn
        var turnMessages = await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turnIndex)
            .ToListAsync(ct);

        // Collect tool result IDs present in this turn
        var toolResultIds = new HashSet<string>(turnMessages
            .Where(m => m.Role == DataModelChatRole.Tool && !string.IsNullOrWhiteSpace(m.ToolCallId))
            .Select(m => m.ToolCallId!)
            .Where(id => !string.IsNullOrWhiteSpace(id))
        );

        var anyChanges = false;

        foreach (var msg in turnMessages)
        {
            if (msg.Role == DataModelChatRole.Assistant && !string.IsNullOrWhiteSpace(msg.ToolCalls))
            {
                try
                {
                    var calls = System.Text.Json.JsonSerializer.Deserialize<List<ChatToolCall>>(msg.ToolCalls!, _jsonOptions) ?? new List<ChatToolCall>();
                    var pruned = calls.Where(tc => !string.IsNullOrWhiteSpace(tc.Id) && toolResultIds.Contains(tc.Id!)).ToList();

                    if (pruned.Count != calls.Count)
                    {
                        msg.ToolCalls = pruned.Count > 0
                            ? System.Text.Json.JsonSerializer.Serialize(pruned, _jsonOptions)
                            : null; // clear when none remain
                        db.Entry(msg).Property(x => x.ToolCalls).IsModified = true;
                        anyChanges = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to prune tool calls for message {MessageId} in conversation {ConversationId} turn {TurnIndex}", msg.Id, conversationId, turnIndex);
                }
            }
        }

        if (anyChanges)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task PersistThinkingBlocksAsync(
        ChatRunOutput? output,
        IReadOnlyList<Guid> assistantMessageIds,
        CancellationToken ct)
    {
        if (output?.Messages == null || assistantMessageIds.Count == 0)
        {
            return;
        }

        var assistantMessages = output.Messages
            .Where(m => m.Role == ChatMessageRole.Assistant)
            .ToList();

        if (assistantMessages.Count < assistantMessageIds.Count)
        {
            return;
        }

        var recentAssistantMessages = assistantMessages
            .Skip(assistantMessages.Count - assistantMessageIds.Count)
            .ToList();

        var updates = new List<(Guid Id, string ThinkingJson)>();
        for (var i = 0; i < assistantMessageIds.Count; i++)
        {
            var thinkingBlocks = recentAssistantMessages[i].ThinkingBlocks;
            if (thinkingBlocks is not { Count: > 0 })
            {
                continue;
            }

            var json = JsonSerializer.Serialize(thinkingBlocks, _jsonOptions);
            updates.Add((assistantMessageIds[i], json));
        }

        if (updates.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var update in updates)
        {
            var stub = new NotebookConversationMessage { Id = update.Id };
            db.Attach(stub);
            stub.ThinkingBlocksJson = update.ThinkingJson;
            db.Entry(stub).Property(x => x.ThinkingBlocksJson).IsModified = true;
        }

        await db.SaveChangesAsync(ct);
    }

    private void EmitThinkingMessages(
        ChatRunOutput? output,
        IReadOnlyList<Guid> assistantMessageIds,
        ChannelWriter<StreamingEvent> writer)
    {
        if (output?.Messages == null || assistantMessageIds.Count == 0)
        {
            return;
        }

        var assistantMessages = output.Messages
            .Where(m => m.Role == ChatMessageRole.Assistant)
            .ToList();

        if (assistantMessages.Count < assistantMessageIds.Count)
        {
            return;
        }

        var recentAssistantMessages = assistantMessages
            .Skip(assistantMessages.Count - assistantMessageIds.Count)
            .ToList();

        foreach (var message in recentAssistantMessages)
        {
            if (message.ThinkingBlocks is not { Count: > 0 })
            {
                continue;
            }

            foreach (var block in message.ThinkingBlocks)
            {
                var content = FormatThinkingDisplay(block);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var payload = new
                {
                    role = "assistant",
                    content,
                    timestamp = DateTime.UtcNow
                };
                writer.TryWrite(new StreamingEvent("assistant_message", JsonSerializer.Serialize(payload, _jsonOptions)));
            }
        }
    }

    #region URL sanitization helpers

    /// <summary>
    /// Converts sandbox:/ URLs to relative paths.
    /// LLMs sometimes generate sandbox:/path/to/file URLs for new files created in the container.
    /// These need to be converted to relative paths like ./Output/file.png
    /// </summary>
    private static string ConvertSandboxUrlsToRelative(string content)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("sandbox:", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        // Global replacement for any sandbox:/ usage (markdown links, text, JSON, HTML attributes)
        // We match until we hit a character that usually terminates a URL or string in these contexts:
        // ] (end of markdown text)
        // ) (end of markdown url)
        // " (end of html/json string)
        // ' (end of single quoted html)
        // < (end of html tag)
        // > (end of html tag - rare but safe)
        // \s (whitespace)
        //
        // NOTE: This assumes sandbox paths do not contain spaces (unless encoded).
        var pattern = new Regex(
            @"sandbox:/(?<path>[^\])""'\s<>]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var result = pattern.Replace(content, m =>
        {
            var path = m.Groups["path"].Value;
            var relativePath = NormalizeSandboxPath(path);
            return relativePath;
        });

        return result;
    }

    /// <summary>
    /// Normalizes a sandbox path to a relative notebook path.
    /// Strips container-specific prefixes like /app/, /app/ContentFiles/{projectSlug}/{notebookSlug}/
    /// </summary>
    private static string NormalizeSandboxPath(string sandboxPath)
    {
        var path = sandboxPath.TrimStart('/');

        // Strip /app/ prefix if present
        if (path.StartsWith("app/", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Substring(4);
        }

        // Strip ContentFiles/{projectSlug}/{notebookSlug}/ prefix if present.
        // Also supports legacy ContentFiles/{guid}/notebooks/{guid}/.
        var contentFilesPattern = new Regex(
            @"^ContentFiles/([^/]+/notebooks/[^/]+|[^/]+/[^/]+)/",
            RegexOptions.IgnoreCase);
        path = contentFilesPattern.Replace(path, "");

        // Ensure we have a valid relative path
        if (string.IsNullOrEmpty(path))
        {
            return "./";
        }

        // Return as relative path
        return "./" + path;
    }

    private static Dictionary<string, string> ExtractFilenameUrlMapFromToolMessage(string toolMessageContent, Guid projectId, Guid notebookId, string? hostUrl)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(toolMessageContent)) return map;

        string textToScan = toolMessageContent;
        try
        {
            using var doc = JsonDocument.Parse(toolMessageContent);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("StandardOutput", out var so) && so.ValueKind == JsonValueKind.String)
            {
                textToScan = so.GetString() ?? toolMessageContent;
            }
        }
        catch
        {
            // Not JSON, scan raw string
        }

        var lines = textToScan.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var normalizedHost = string.IsNullOrWhiteSpace(hostUrl) ? null : new Uri(hostUrl).GetLeftPart(UriPartial.Authority).TrimEnd('/');
        for (int i = 0; i < lines.Length; i++)
        {
            var header = lines[i].Trim();
            var isNew = header.Equals("New Files", StringComparison.OrdinalIgnoreCase);
            var isModified = header.Equals("Modified Files", StringComparison.OrdinalIgnoreCase);
            if (!isNew && !isModified) continue;

            int j = i + 1;
            // Skip optional blank lines
            while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j])) j++;
            // Expect '---' delimiter
            if (j < lines.Length && lines[j].Trim().Equals("---", StringComparison.Ordinal)) j++;

            // Collect URL lines until blank line or end
            for (; j < lines.Length; j++)
            {
                var line = lines[j].Trim();
                if (string.IsNullOrWhiteSpace(line)) break;

                if (!string.IsNullOrWhiteSpace(normalizedHost) && line.StartsWith(normalizedHost, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) continue;

                    string? filename = null;
                    var query = uri.Query.TrimStart('?');
                    foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var idx = pair.IndexOf('=');
                        if (idx <= 0) continue;
                        var key = pair.Substring(0, idx);
                        if (!key.Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
                        var value = Uri.UnescapeDataString(pair.Substring(idx + 1));
                        filename = Path.GetFileName(value);
                        break;
                    }
                    filename ??= Path.GetFileName(uri.LocalPath);
                    if (!string.IsNullOrWhiteSpace(filename)) map[filename] = line;
                }
                else if (line.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback format when host URL wasn't available in the tool; build canonical URL if possible
                    var rel = line.Substring("File:".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(rel) && !string.IsNullOrWhiteSpace(hostUrl))
                    {
                        var uriBuilder = new UriBuilder(hostUrl);
                        uriBuilder.Path = $"api/projects/{projectId}/notebooks/{notebookId}/files/content";
                        uriBuilder.Query = $"path={Uri.EscapeDataString(rel)}";
                        var built = uriBuilder.Uri.ToString();
                        var filename = Path.GetFileName(rel);
                        if (!string.IsNullOrWhiteSpace(filename)) map[filename] = built;
                    }
                }
            }
        }

        return map;
    }

    private static string SanitizeAssistantContent(string content, IDictionary<string, string> filenameUrlMap, string? hostUrl)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        // First, convert any sandbox:/ URLs to relative paths (LLM sometimes generates these for new files)
        string result = ConvertSandboxUrlsToRelative(content);

        if (filenameUrlMap.Count == 0)
        {
            // Even if no filename map, convert absolute URLs to relative paths for portability
            return Utils.MarkdownUrlConverter.ConvertAbsoluteToRelative(result);
        }
        // Use a stable per-call timestamp so all URLs in this message share the same marker
        var messageStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        foreach (var kv in filenameUrlMap)
        {
            var filename = kv.Key;
            var canonicalUrl = kv.Value;
            var canonicalUrlWithStamp = AppendQueryParamIfMissing(canonicalUrl, "m", messageStamp);

            // 1) Markdown links/images: [text](url) or ![alt](url)
            // Replace only the URL inside the parentheses when it contains the filename
            var mdPattern = new Regex(@"(?<head>(!\[[^\]]*\]|\[[^\]]*\])\()(?<url>[^)]+)(?<tail>\))", RegexOptions.Compiled);
            result = mdPattern.Replace(result, m =>
            {
                var url = m.Groups["url"].Value;
                return url.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0 && !url.Equals(canonicalUrlWithStamp, StringComparison.OrdinalIgnoreCase)
                    ? m.Groups["head"].Value + canonicalUrlWithStamp + m.Groups["tail"].Value
                    : m.Value;
            });

            // 2) HTML anchors/images: href="url" or src="url" (double-quoted)
            var htmlAttrPattern = new Regex(@"(?<attr>href|src)\s*=\s*""(?<url>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            result = htmlAttrPattern.Replace(result, m =>
            {
                var url = m.Groups["url"].Value;
                return url.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0 && !url.Equals(canonicalUrlWithStamp, StringComparison.OrdinalIgnoreCase)
                    ? m.Value.Replace(url, canonicalUrlWithStamp)
                    : m.Value;
            });

            // 3) HTML anchors/images: href='url' or src='url' (single-quoted)
            var htmlAttrPatternSingle = new Regex(@"(?<attr>href|src)\s*=\s*'(?<url>[^']+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            result = htmlAttrPatternSingle.Replace(result, m =>
            {
                var url = m.Groups["url"].Value;
                return url.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0 && !url.Equals(canonicalUrlWithStamp, StringComparison.OrdinalIgnoreCase)
                    ? m.Value.Replace(url, canonicalUrlWithStamp)
                    : m.Value;
            });
        }

        // Convert absolute notebook file URLs to relative paths for portability
        // This happens AFTER sanitization to ensure all URLs are converted
        result = Utils.MarkdownUrlConverter.ConvertAbsoluteToRelative(result);

        return result;
    }

    private static string AppendQueryParamIfMissing(string url, string key, string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            if (url.Contains(key + "=", StringComparison.OrdinalIgnoreCase)) return url;

            var hasQuestion = url.Contains("?");
            var separator = hasQuestion ? "&" : "?";
            return url + separator + key + "=" + Uri.EscapeDataString(value);
        }
        catch
        {
            // Best-effort append if parsing fails
            var separator = url.Contains("?") ? "&" : "?";
            return url + separator + key + "=" + Uri.EscapeDataString(value);
        }
    }

    public async Task<PagedUserConversationsDto> GetUserConversationsAsync(UserConversationsQuery query)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();



        // Normalize query parameters
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Max(1, Math.Min(100, query.PageSize)); // Limit to 100 max
        var searchTerm = query.Search?.Trim();
        var sortBy = query.SortBy?.ToLower() ?? "date";
        var sortOrder = query.SortOrder?.ToLower() ?? "desc";

        // Optimized approach: Start from messages where user participated, 
        // then get distinct conversations. This leverages the (UserId, NotebookConversationId) index.
        // Avoids loading all messages via Include() which was causing 3+ second queries.

        // Step 1: Get conversation IDs where user has authored at least one message
        // Uses index: IX_NotebookConversationMessages_UserId_NotebookConversationId
        var userConversationIds = db.NotebookConversationMessages
            .Select(m => m.NotebookConversationId)
            .Distinct();

        // Step 2: Build efficient query with projections (no Include needed)
        var queryable = db.NotebookConversations
            .Where(c => userConversationIds.Contains(c.Id) && !c.Notebook.Project.Deleted)
            .Select(c => new
            {
                c.Id,
                c.Title,
                NotebookId = c.NotebookId,
                NotebookTitle = c.Notebook.Title,
                ProjectId = c.Notebook.ProjectId,
                ProjectTitle = c.Notebook.Project.Title,
                c.Created,
                // Subquery for LastActivity - EF translates to efficient SQL
                LastActivity = db.NotebookConversationMessages
                    .Where(m => m.NotebookConversationId == c.Id)
                    .Max(m => (DateTime?)m.Created) ?? c.Created
            });

        // Apply search filter (case-insensitive)
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var searchLower = searchTerm.ToLower();
            queryable = queryable.Where(c =>
                c.Title.ToLower().Contains(searchLower) ||
                c.NotebookTitle.ToLower().Contains(searchLower) ||
                c.ProjectTitle.ToLower().Contains(searchLower)
            );
        }

        // Apply sorting
        queryable = sortBy switch
        {
            "project" => sortOrder == "asc"
                ? queryable.OrderBy(c => c.ProjectTitle).ThenByDescending(c => c.LastActivity)
                : queryable.OrderByDescending(c => c.ProjectTitle).ThenByDescending(c => c.LastActivity),
            "notebook" => sortOrder == "asc"
                ? queryable.OrderBy(c => c.NotebookTitle).ThenByDescending(c => c.LastActivity)
                : queryable.OrderByDescending(c => c.NotebookTitle).ThenByDescending(c => c.LastActivity),
            _ => sortOrder == "asc" // date
                ? queryable.OrderBy(c => c.LastActivity)
                : queryable.OrderByDescending(c => c.LastActivity)
        };

        // Get total count before pagination
        var totalCount = await queryable.CountAsync();

        // Apply pagination
        var items = await queryable
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new UserConversationDto(
                c.Id,
                c.Title,
                c.NotebookId,
                c.NotebookTitle,
                c.ProjectId,
                c.ProjectTitle,
                c.Created,
                c.LastActivity
            ))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        return new PagedUserConversationsDto(
            Items: items,
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize,
            TotalPages: totalPages
        );
    }
    #endregion
}
