using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using GuideAnts.Usage;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations;

public class PublishedConversationService : IPublishedConversationService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IContextOptionsService _contextOptionsService;
	private readonly INotebookFileService? _notebookFileService;
	private readonly INotebookFileSyncService? _notebookFileSyncService;
	private readonly IUsageRecorder _usageRecorder;
	private readonly IConfiguration? _configuration;
	private readonly ILogger<PublishedConversationService> _logger;
	private readonly IChatCompletionClientFactory _chatClientFactory;
	private readonly IOptions<MarkdownAttachmentOptions> _markdownAttachmentOptions;
	private readonly IChatModelResolver _chatModelResolver;

	private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	public PublishedConversationService(
		IServiceScopeFactory scopeFactory,
		IHttpClientFactory httpClientFactory,
		IContextOptionsService contextOptionsService,
		IChatCompletionClientFactory chatClientFactory,
		IUsageRecorder usageRecorder,
		ILogger<PublishedConversationService> logger,
		IOptions<MarkdownAttachmentOptions> markdownAttachmentOptions,
		IChatModelResolver chatModelResolver,
		IConfiguration? configuration = null,
		INotebookFileService? notebookFileService = null,
		INotebookFileSyncService? notebookFileSyncService = null)
	{
		_scopeFactory = scopeFactory;
		_httpClientFactory = httpClientFactory;
		_contextOptionsService = contextOptionsService;
		_chatClientFactory = chatClientFactory;
		_usageRecorder = usageRecorder;
		_logger = logger;
		_markdownAttachmentOptions = markdownAttachmentOptions ?? throw new ArgumentNullException(nameof(markdownAttachmentOptions));
		_chatModelResolver = chatModelResolver ?? throw new ArgumentNullException(nameof(chatModelResolver));
		_configuration = configuration;
		_notebookFileService = notebookFileService;
		_notebookFileSyncService = notebookFileSyncService;
	}

	public async Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var notebookExists = await db.Notebooks.AnyAsync(n => n.Id == notebookId);
        if (!notebookExists) throw new KeyNotFoundException("Notebook not found");

        var conv = new NotebookConversation
        {
            NotebookId = notebookId,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim()
        };
        db.NotebookConversations.Add(conv);
        await db.SaveChangesAsync();
        return new NotebookConversationListDto(conv.Id, conv.Title, conv.Created, conv.Created);
    }

    public async Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var conversationData = await db.NotebookConversations
            .Where(c => c.Id == conversationId)
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
                        m.ToolCalls,
                        m.ToolCallId,
                        m.FunctionName,
                        m.MessageContentType,
                        Attachments = m.Attachments.OrderBy(a => a.OrderIndex)
                            .Select(a => new
                            {
                                a.NotebookFileId,
                                FileName = a.NotebookFile != null ? a.NotebookFile.RelativePath : "unknown",
                                FileSize = a.NotebookFile != null ? a.NotebookFile.FileSize : 0,
                                a.Type
                            }).ToList()
                    }).ToList()
            })
            .FirstOrDefaultAsync();

        if (conversationData == null) return null;

        var messageDtos = new List<MessageDto>();
        foreach (var msg in conversationData.Messages)
        {
            IReadOnlyList<ToolCallDto>? toolCalls = null;
            if (!string.IsNullOrEmpty(msg.ToolCalls))
            {
                try
                {
                    var openAiToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(msg.ToolCalls, _json);
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
                catch { }
            }

            var attachments = msg.Attachments.Select(a => new AttachedFileDto(
                a.NotebookFileId,
                Path.GetFileName(a.FileName),
                DetermineFileTypeString(a.FileName),
                a.FileSize,
                null,
                a.Type
            )).ToList();

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
                msg.MessageContentType
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
        
        var conv = await db.NotebookConversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversationId);
            
        if (conv == null) return;

        var lastUserMessage = conv.Messages
            .Where(m => m.Role == DataModelChatRole.User)
            .OrderByDescending(m => m.TurnIndex)
            .ThenByDescending(m => m.MessageSequence)
            .FirstOrDefault();

        if (lastUserMessage == null) return;

        var messagesToRemove = conv.Messages
            .Where(m => m.TurnIndex >= lastUserMessage.TurnIndex)
            .ToList();

        var turnsToRemove = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId)
            .Where(t => t.TurnIndex >= lastUserMessage.TurnIndex)
            .ToListAsync();

        db.NotebookConversationMessages.RemoveRange(messagesToRemove);
        db.ConversationTurns.RemoveRange(turnsToRemove);
        await db.SaveChangesAsync();
    }

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

    public async IAsyncEnumerable<StreamingEvent> ResumeAfterExternalToolResultsStreamAsync(Guid conversationId, string? publisherId, string? externalUserIdentity, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<StreamingEvent>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var writer = channel.Writer;
		// Throttling removed to avoid disposal races while events are still in-flight

        NotebookConversation? dbConversation = null;
        ConversationTurn? dbTurn = null;
        var currentMessageSequence = 2; // user is 1
        var assistantMessageIds = new List<Guid>();
        var thinkingEmittedInStream = false;
        var filenameToPublishedUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hostUrl = _configuration?["ANTRUNNER_SERVICES_HOST_URL"] ?? Environment.GetEnvironmentVariable("ANTRUNNER_SERVICES_HOST_URL");

        try
        {
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
                    .FirstOrDefault();

                // Determine next message sequence within the last turn (resume path)
                if (dbTurn != null)
                {
                    var maxExistingSequence = await db.NotebookConversationMessages
                        .Where(m => m.NotebookConversationId == dbConversation.Id
                                    && m.TurnIndex == dbTurn.TurnIndex)
                        .MaxAsync(m => (int?)m.MessageSequence, cancellationToken) ?? 1;
                    currentMessageSequence = maxExistingSequence + 1;
                }
            }

            var assistantName = dbConversation!.Notebook.Guide?.Name
                ?? throw new InvalidOperationException("Notebook Guide.Name is required for published conversations.");

            // Handlers mirroring SendMessageStreamAsync
            StreamingMessageProgressEventHandler onProgress = (_, e) =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (string.Equals(e.Role, "assistant_thinking", StringComparison.OrdinalIgnoreCase))
                {
                    thinkingEmittedInStream = true;
                }
                var payload = new { role = "assistant", contentDelta = e.ContentDelta, timestamp = DateTime.UtcNow };
                var ev = new StreamingEvent("assistant_message", JsonSerializer.Serialize(payload, _json));
				try { if (!cancellationToken.IsCancellationRequested) writer.TryWrite(ev); } catch { }
            };

            MessageAddedEventHandler onMessageAdded = (_, e) =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (string.IsNullOrEmpty(e.Role)) return;

                if (e.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var sanitizedAssistant = SanitizePublishedAssistantContent(
                        e.Message,
                        filenameToPublishedUrl,
                        dbConversation!.Notebook.ProjectId,
                        dbConversation.NotebookId,
                        dbConversation.Id,
                        publisherId,
                        hostUrl);
                    var msg = new NotebookConversationMessage
                    {
                        NotebookConversationId = dbConversation!.Id,
                        TurnIndex = dbTurn!.TurnIndex,
                        MessageSequence = currentMessageSequence++,
                        Role = DataModelChatRole.Assistant,
                        AssistantName = assistantName,
                        Content = sanitizedAssistant,
                        IsStreaming = false,
                        Created = DateTime.UtcNow
                    };
                    if (!string.IsNullOrEmpty(e.ToolCallsJson))
                    {
                        msg.ToolCalls = e.ToolCallsJson;
                    }
                    db.NotebookConversationMessages.Add(msg);
                    var turn = db.ConversationTurns.First(t => t.Id == dbTurn!.Id);
                    turn.LastUpdated = DateTime.UtcNow;
                    db.SaveChanges();
                    assistantMessageIds.Add(msg.Id);
                }
                else if (e.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var toolMessage = new NotebookConversationMessage
                    {
                        NotebookConversationId = dbConversation!.Id,
                        TurnIndex = dbTurn!.TurnIndex,
                        MessageSequence = currentMessageSequence++,
                        Role = DataModelChatRole.Tool,
                        Content = e.Message,
                        ToolCallId = e.ToolCallId,
                        FunctionName = e.FunctionName,
                        IsStreaming = false,
                        Created = DateTime.UtcNow
                    };
                    db.NotebookConversationMessages.Add(toolMessage);
                    var turn = db.ConversationTurns.First(t => t.Id == dbTurn!.Id);
                    turn.LastUpdated = DateTime.UtcNow;
                    db.SaveChanges();
                }

                // Emit to stream
                var eventType = e.Role!.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                    ? StreamingEventTypes.Message
                    : DetermineEventType(e.Role!, e.Message!);
                var payload = new { role = e.Role!.ToLowerInvariant(), content = e.Message, timestamp = DateTime.UtcNow };
                var ev = new StreamingEvent(eventType, JsonSerializer.Serialize(payload, _json));
				try { writer.TryWrite(ev); } catch { }
            };

            // Build messages from DB (no client context on resume)
            var previousMessages = await BuildMessagesForAssistantAsync(dbConversation!, assistantName, null, cancellationToken);

            // Execute deferred server-handled tools for last assistant message
            List<ChatToolCall> lastToolCalls = new();
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var lastAssistant = await db.NotebookConversationMessages
                    .Where(m => m.NotebookConversationId == dbConversation!.Id && m.Role == DataModelChatRole.Assistant)
                    .OrderByDescending(m => m.Created)
                    .FirstOrDefaultAsync(cancellationToken);
                if (lastAssistant != null && !string.IsNullOrEmpty(lastAssistant.ToolCalls))
                {
                    try
                    {
                        lastToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(lastAssistant.ToolCalls, _json) ?? new();
                    }
                    catch { lastToolCalls = new(); }
                }
            }

            var assistantDefForResume = await AssistantUtility.GetAssistantCreateRequest(assistantName)
                ?? throw new InvalidOperationException($"Assistant definition not found for {assistantName}");

            if (lastToolCalls.Count > 0)
            {
                var httpClient = _httpClientFactory.CreateClient();
                await AntRunner.Chat.ThreadRun.DoToolCalls(
                    assistantDefForResume,
                    lastToolCalls,
                    previousMessages,
                    oAuthUserAccessToken: null,
                    httpClient: httpClient,
                    messageAdded: onMessageAdded,
                    ctx: new AntRunner.ToolCalling.InvocationContext(
                        ProjectId: dbConversation.Notebook.ProjectId,
                        NotebookId: dbConversation.NotebookId,
                        ConversationId: dbConversation.Id,
                        OAuthUserAccessToken: null));
            }

            var resolvedResume = _chatModelResolver.Resolve(assistantDefForResume.Model);

            // Continue LLM run without adding a new user message
            var continueTask = AntRunner.Chat.ThreadRun.ExecuteAsync(
                new ChatRunOptions
                {
                    AssistantName = assistantName,
                    Instructions = string.Empty,
                    DeploymentId = resolvedResume.ModelId,
                    ExecutionPolicy = resolvedResume.ExecutionPolicy
                },
                _chatClientFactory,
                previousMessages,
                _httpClientFactory.CreateClient(),
                onMessageAdded,
                onProgress,
                onExternalToolCall: (_, evt) =>
                {
                    try
                    {
                        var payload = new { toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(evt.ToolCallsJson, _json) };
                        writer.TryWrite(new StreamingEvent(StreamingEventTypes.ExternalToolCall, JsonSerializer.Serialize(payload, _json)));
                    }
                    catch
                    {
                        writer.TryWrite(new StreamingEvent(StreamingEventTypes.ExternalToolCall, evt.ToolCallsJson));
                    }
                },
                resumeWithoutNewUserMessage: true,
                ctx: new AntRunner.ToolCalling.InvocationContext(
                    ProjectId: dbConversation.Notebook.ProjectId,
                    NotebookId: dbConversation.NotebookId,
                    ConversationId: dbConversation.Id,
                    OAuthUserAccessToken: null),
                token: cancellationToken,
                isAgentInvocation: false);

            _ = Task.Run(async () =>
            {
                try
                {
                    var output = await continueTask;

                    if (output?.Status != null && output.Status.Equals("pending_client_tool", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var scope = _scopeFactory.CreateScope())
                        {
                            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            var turn = db.ConversationTurns.First(tx => tx.Id == dbTurn!.Id);
                            // Keep status as 'streaming' while awaiting client tool
                            if (!string.Equals(turn.Status, "streaming", StringComparison.OrdinalIgnoreCase))
                            {
                                turn.Status = "streaming";
                            }
                            turn.LastUpdated = DateTime.UtcNow;
                            db.SaveChanges();
                        }

                        writer.TryWrite(new StreamingEvent(StreamingEventTypes.PendingClientTool, "{}"));
                        return;
                    }

                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var turn = db.ConversationTurns.First(tx => tx.Id == dbTurn!.Id);
                        turn.Status = "completed";
                        turn.LastUpdated = DateTime.UtcNow;
                        turn.ChatRunOutputJson = output != null ? JsonSerializer.Serialize(output, _json) : null;
                        turn.UsageJson = output?.Usage != null ? JsonSerializer.Serialize(output.Usage, _json) : null;
                        
                        // Store file changes tracked during tool execution
                        if (output?.NewFiles != null && output.NewFiles.Count > 0)
                        {
                            turn.FilesCreated = JsonSerializer.Serialize(output.NewFiles, _json);
                        }
                        if (output?.ModifiedFiles != null && output.ModifiedFiles.Count > 0)
                        {
                            turn.FilesModified = JsonSerializer.Serialize(output.ModifiedFiles, _json);
                        }
                        
                        db.SaveChanges();
                    }

                    await PersistThinkingBlocksAsync(output, assistantMessageIds, CancellationToken.None);
                    if (!thinkingEmittedInStream)
                    {
                        EmitThinkingMessages(output, assistantMessageIds, writer);
                    }

                    // Attribute usage to the underlying guide/assistant
                    Guid? assistantId = dbConversation!.Notebook.GuideId ?? dbConversation.Notebook.NotebookTemplateId;
                    string? modelDeploymentId = dbTurn?.ModelDeploymentId;

                    // Record chat usage if available
                    if (output?.Usage != null)
                    {
                        try
                        {
                            var usagePayload = new { promptTokens = output.Usage.PromptTokens, completionTokens = output.Usage.CompletionTokens, totalTokens = output.Usage.TotalTokens };
                            writer.TryWrite(new StreamingEvent(StreamingEventTypes.Usage, JsonSerializer.Serialize(usagePayload, _json)));

                            var cached = output.Usage.CachedPromptTokens ?? 0;
                            var prompt = output.Usage.PromptTokens ?? 0;
                            var completion = output.Usage.CompletionTokens ?? 0;
                            var metrics = new UsageMetrics(prompt, cached, 0, completion);
                            var usageService = LlmProviderResolver.ResolveUsageServiceName(modelDeploymentId, _scopeFactory);

                            Guid? lastAssistantMessageId = null;
                            using (var scopeUsage = _scopeFactory.CreateScope())
                            {
                                var dbUsage = scopeUsage.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                                lastAssistantMessageId = await dbUsage.NotebookConversationMessages
                                    .Where(m => m.NotebookConversationId == dbConversation.Id && m.Role == DataModelChatRole.Assistant)
                                    .OrderByDescending(m => m.Created)
                                    .Select(m => (Guid?)m.Id)
                                    .FirstOrDefaultAsync(CancellationToken.None);
                            }

							await _usageRecorder.RecordChatAsync(
								projectId: dbConversation.Notebook.ProjectId,
								notebookId: dbConversation.NotebookId,
								service: usageService,
								modelDeploymentId: modelDeploymentId ?? string.Empty,
								metrics: metrics,
								conversationId: dbConversation.Id,
								metadataJson: null,
								assistantId: assistantId,
								notebookConversationMessageId: lastAssistantMessageId,
								ct: CancellationToken.None);
                        }
                        catch { /* best-effort */ }
                    }

                    // Record tool call usage for all tool messages in this turn (query DB like ConversationService)
                    // Skip any already recorded (handles multiple resume cycles within a single turn)
                    try
                    {
                        using var scopeTools = _scopeFactory.CreateScope();
                        var dbTools = scopeTools.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                        var toolMessages = await dbTools.NotebookConversationMessages
                            .Where(m => m.NotebookConversationId == dbConversation!.Id
                                     && m.TurnIndex == dbTurn!.TurnIndex
                                     && m.Role == DataModelChatRole.Tool
                                     && m.FunctionName != null)
                            .Select(m => new { m.Id, m.FunctionName, m.ToolCallId, ContentLength = m.Content != null ? m.Content.Length : 0 })
                            .ToListAsync(CancellationToken.None);

                        // Get IDs of tool messages that already have usage events recorded
                        var toolMessageIds = toolMessages.Select(m => m.Id).ToList();
                        var alreadyRecordedIds = await dbTools.UsageEvents
                            .Where(u => u.NotebookConversationMessageId != null
                                     && toolMessageIds.Contains(u.NotebookConversationMessageId.Value)
                                     && u.Category == GuideAntsApi.DataModel.Models.UsageCategory.ToolCall)
                            .Select(u => u.NotebookConversationMessageId!.Value)
                            .ToListAsync(CancellationToken.None);
                        var alreadyRecordedSet = new HashSet<Guid>(alreadyRecordedIds);

                        foreach (var toolMsg in toolMessages)
                        {
                            // Skip crew bridge invocations - they record their own usage
                            if (string.Equals(toolMsg.FunctionName, "InvokeAgent", StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Skip if already recorded (from a previous resume cycle)
                            if (alreadyRecordedSet.Contains(toolMsg.Id))
                                continue;

                            await _usageRecorder.RecordToolCallAsync(
                                projectId: dbConversation!.Notebook.ProjectId,
                                notebookId: dbConversation.NotebookId,
                                conversationId: dbConversation.Id,
                                functionName: toolMsg.FunctionName!,
                                metadataJson: JsonSerializer.Serialize(new
                                {
                                    toolCallId = toolMsg.ToolCallId,
                                    functionName = toolMsg.FunctionName,
                                    contentLength = toolMsg.ContentLength
                                }),
                                assistantId: assistantId,
                                notebookConversationMessageId: toolMsg.Id,
                                ct: CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to record tool call usage in ResumeAfterExternalToolResultsStreamAsync");
                    }

                    writer.TryWrite(new StreamingEvent(StreamingEventTypes.Complete, "{}"));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Published conversation stream failed for conversation {ConversationId}", dbConversation?.Id);
                    writer.TryWrite(new StreamingEvent(
                        StreamingEventTypes.Error,
                        JsonSerializer.Serialize(StreamingErrorEnvelope.Build(ex), _json)));
                }
                finally
                {
                    writer.Complete();
                }
            });
        }
        finally
        {
            // lifecycle managed in continuation
        }

        await foreach (var ev in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return ev;
        }
    }

	public async IAsyncEnumerable<StreamingEvent> SendMessageStreamAsync(Guid conversationId, SendMessageRequest request, string? publisherId, string? externalUserIdentity, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		// Allow empty instructions if there are attachments (attachments provide the context)
		if (string.IsNullOrWhiteSpace(request.Instructions) && (request.Attachments == null || request.Attachments.Count == 0))
			throw new ArgumentException("Instructions required", nameof(request));

		var channel = Channel.CreateBounded<StreamingEvent>(new BoundedChannelOptions(100)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = true,
			SingleWriter = false
		});
		var writer = channel.Writer;
		// Throttling removed to avoid disposal races while events are still in-flight

		NotebookConversation? dbConversation = null;
		ConversationTurn? dbTurn = null;
		Guid? currentAssistantMessageId = null;
		var assistantMessageIds = new List<Guid>();
		var thinkingEmittedInStream = false;
		var currentAssistantContent = new System.Text.StringBuilder();
		var currentMessageSequence = 2; // user is 1
		// no streamingSucceeded tracking needed in published flow
		var filenameToPublishedUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var hostUrl = _configuration?["ANTRUNNER_SERVICES_HOST_URL"] ?? Environment.GetEnvironmentVariable("ANTRUNNER_SERVICES_HOST_URL");

		// Tool usage recording is done at the END of the run by querying the DB (parity with ConversationService).
		// We no longer use an in-memory list during streaming callbacks.

		try
		{
			// Load conversation and context
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
			}

			// Load PublishedGuide and enforce limits
			DataModel.Models.PublishedGuide? publishedGuide = null;
			if (!string.IsNullOrWhiteSpace(publisherId) && Guid.TryParse(publisherId, out var pubGuid))
			{
				using (var scope = _scopeFactory.CreateScope())
				{
					var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
					publishedGuide = await db.PublishedGuides
						.AsNoTracking()
						.FirstOrDefaultAsync(pg => pg.Id == pubGuid && pg.Active, cancellationToken);
				}
			}

			// Enforce MaxUserMessageLength
			if (publishedGuide?.MaxUserMessageLength.HasValue == true)
			{
				if (request.Instructions.Length > publishedGuide.MaxUserMessageLength.Value)
				{
					throw new InvalidOperationException(
						$"Message exceeds maximum length of {publishedGuide.MaxUserMessageLength} characters");
				}
			}

			// Enforce MaxTurns
			if (publishedGuide?.MaxTurns.HasValue == true)
			{
				var currentTurnCount = dbConversation.Turns.Count;
				if (currentTurnCount >= publishedGuide.MaxTurns.Value)
				{
					throw new InvalidOperationException(
						$"Conversation has reached maximum of {publishedGuide.MaxTurns} turns");
				}
			}

			// Determine assistant strictly from the Guide (client does not send AssistantName)
			var assistantName = dbConversation.Notebook.Guide?.Name
				?? throw new InvalidOperationException("Notebook Guide.Name is required for published conversations.");
			string? modelDeploymentId = request.ModelDeploymentId;

			if (string.IsNullOrWhiteSpace(modelDeploymentId))
			{
				var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName)
					?? throw new InvalidOperationException($"Assistant definition not found for {assistantName}.");
				modelDeploymentId = assistantDef.Model;
			}

			var resolvedPublished = _chatModelResolver.Resolve(modelDeploymentId);
			modelDeploymentId = resolvedPublished.ModelId;

			// Prepare previous messages (system + history). No user context.
			var previousMessages = await BuildMessagesForAssistantAsync(dbConversation, assistantName, request.ClientContext, cancellationToken);

			// Create turn and user message
			using (var scope = _scopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

				var turnIndex = await db.ConversationTurns
					.Where(t => t.NotebookConversationId == dbConversation.Id)
					.MaxAsync(t => (int?)t.TurnIndex, cancellationToken) ?? 0;
				turnIndex++;

				dbTurn = new ConversationTurn
				{
					NotebookConversationId = dbConversation.Id,
					TurnIndex = turnIndex,
					AssistantName = assistantName,
					ModelDeploymentId = modelDeploymentId,
					Instructions = request.Instructions,
					Created = DateTime.UtcNow,
					Status = "streaming",
					LastUpdated = DateTime.UtcNow
				};
				db.ConversationTurns.Add(dbTurn);
				await db.SaveChangesAsync(cancellationToken);

				var userMessage = new NotebookConversationMessage
				{
					NotebookConversationId = dbConversation.Id,
					TurnIndex = turnIndex,
					MessageSequence = 1,
					Role = DataModelChatRole.User,
					Content = request.Instructions,
					AssistantName = "user",
					ModelDeploymentId = modelDeploymentId,
					UserId = null, // published has no user
					ExternalUserIdentity = externalUserIdentity,
					Created = DateTime.UtcNow
				};
				db.NotebookConversationMessages.Add(userMessage);
				await db.SaveChangesAsync(cancellationToken);

				if (request.Attachments != null && request.Attachments.Count > 0)
				{
					await AddAttachmentsToUserMessageAsync(db, userMessage.Id, dbConversation.NotebookId, request.Attachments, cancellationToken);
					foreach (var attachment in request.Attachments)
					{
						var messages = await CreateOpenAiMessagesFromNotebookFileAsync(db, attachment.NotebookFileId, cancellationToken);
						foreach (var m in messages) previousMessages.Add(m);
					}
				}
			}

			// For published flow, no broadcast hub events and no locking/user identity

			// Build chat options
			var chatOptions = new ChatRunOptions
			{
				AssistantName = assistantName,
				DeploymentId = modelDeploymentId,
				Instructions = request.Instructions,
				ExternalAuthTokens = request.ExternalAuthTokens,
				ExecutionPolicy = resolvedPublished.ExecutionPolicy
			};

			// Handlers
			StreamingMessageProgressEventHandler onProgress = (_, e) =>
			{
				if (cancellationToken.IsCancellationRequested) return;
				if (string.Equals(e.Role, "assistant_thinking", StringComparison.OrdinalIgnoreCase))
				{
					var thinkingPayload = new { role = "assistant", content = e.ContentDelta, timestamp = DateTime.UtcNow };
					var thinkingEvent = new StreamingEvent("assistant_message", JsonSerializer.Serialize(thinkingPayload, _json));
					try { if (!cancellationToken.IsCancellationRequested) writer.TryWrite(thinkingEvent); } catch { }
					thinkingEmittedInStream = true;
					return;
				}

				// If we don't have a current assistant message, create one for streaming
				if (currentAssistantMessageId == null)
				{
					using var scope = _scopeFactory.CreateScope();
					var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
					var msg = new NotebookConversationMessage
					{
						NotebookConversationId = dbConversation!.Id,
						TurnIndex = dbTurn!.TurnIndex,
						MessageSequence = currentMessageSequence++,
						Role = DataModelChatRole.Assistant,
						AssistantName = assistantName,
						ModelDeploymentId = modelDeploymentId,
						Content = string.Empty,
						IsStreaming = true,
						Created = DateTime.UtcNow
					};
					db.NotebookConversationMessages.Add(msg);
					var turn = db.ConversationTurns.First(t => t.Id == dbTurn!.Id);
					turn.LastUpdated = DateTime.UtcNow;
					db.SaveChanges();
					currentAssistantMessageId = msg.Id;
					assistantMessageIds.Add(msg.Id);
				}

				currentAssistantContent.Append(e.ContentDelta);

				var payload = new { role = "assistant", contentDelta = e.ContentDelta };
				var ev = new StreamingEvent("token", JsonSerializer.Serialize(payload, _json));
				try { if (!cancellationToken.IsCancellationRequested) writer.TryWrite(ev); } catch { }
			};

			MessageAddedEventHandler onMessageAdded = (_, e) =>
			{
				if (cancellationToken.IsCancellationRequested) return;
				if (string.IsNullOrEmpty(e.Role)) return;

				if (e.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
				{
					// Handle assistant messages with tool calls: update existing or create new
					if (!string.IsNullOrEmpty(e.ToolCallsJson))
					{
						// Sanitize the tool call assistant text
						var toolCallAssistantText = SanitizePublishedAssistantContent(
							e.Message ?? string.Empty,
							filenameToPublishedUrl,
							dbConversation!.Notebook.ProjectId,
							dbConversation.NotebookId,
							dbConversation.Id,
							publisherId,
							hostUrl);

					var toolCallsJson = e.ToolCallsJson;

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
							var turn = dbUpdate.ConversationTurns.First(t => t.Id == dbTurn!.Id);
							turn.LastUpdated = DateTime.UtcNow;
							dbUpdate.SaveChanges();
						}
						else
						{
							// No streaming message exists, create new one
							var toolCallMessage = new NotebookConversationMessage
							{
								NotebookConversationId = dbConversation!.Id,
								TurnIndex = dbTurn!.TurnIndex,
								MessageSequence = currentMessageSequence++,
								Role = DataModelChatRole.Assistant,
								AssistantName = assistantName,
								ModelDeploymentId = modelDeploymentId,
								Content = toolCallAssistantText,
								ToolCalls = toolCallsJson,
								IsStreaming = false,
								Created = DateTime.UtcNow
							};

							using var scopeCreate = _scopeFactory.CreateScope();
							var dbCreate = scopeCreate.ServiceProvider.GetRequiredService<ApplicationDbContext>();
							dbCreate.NotebookConversationMessages.Add(toolCallMessage);
							var turn = dbCreate.ConversationTurns.First(t => t.Id == dbTurn!.Id);
							turn.LastUpdated = DateTime.UtcNow;
							dbCreate.SaveChanges();
							assistantMessageIds.Add(toolCallMessage.Id);
						}

						// Reset streaming tracking - tool call message is complete
						currentAssistantMessageId = null;
						currentAssistantContent.Clear();
					}
					// Create/complete assistant message rows (no streaming, no tool calls, no finalized message)
					else if (currentAssistantMessageId == null)
					{
						using var scope = _scopeFactory.CreateScope();
						var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
						var sanitizedAssistant = SanitizePublishedAssistantContent(
							e.Message,
							filenameToPublishedUrl,
							dbConversation!.Notebook.ProjectId,
							dbConversation.NotebookId,
							dbConversation.Id,
							publisherId,
							hostUrl);
						var msg = new NotebookConversationMessage
						{
							NotebookConversationId = dbConversation!.Id,
							TurnIndex = dbTurn!.TurnIndex,
							MessageSequence = currentMessageSequence++,
							Role = DataModelChatRole.Assistant,
							AssistantName = assistantName,
							ModelDeploymentId = modelDeploymentId,
							Content = sanitizedAssistant,
							IsStreaming = false,
							Created = DateTime.UtcNow
						};
						db.NotebookConversationMessages.Add(msg);
						var turn = db.ConversationTurns.First(t => t.Id == dbTurn!.Id);
						turn.LastUpdated = DateTime.UtcNow;
						db.SaveChanges();
						assistantMessageIds.Add(msg.Id);
						// Note: Don't set currentAssistantMessageId here - this is a complete message, not streaming
					}
					else
					{
						// Update existing streaming message
						using var scope = _scopeFactory.CreateScope();
						var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
						var stub = new NotebookConversationMessage { Id = currentAssistantMessageId.Value };
						db.Attach(stub);
						stub.Content = SanitizePublishedAssistantContent(
							e.Message,
							filenameToPublishedUrl,
							dbConversation!.Notebook.ProjectId,
							dbConversation.NotebookId,
							dbConversation.Id,
							publisherId,
							hostUrl);
						stub.IsStreaming = false;
						db.Entry(stub).Property(x => x.Content).IsModified = true;
						db.Entry(stub).Property(x => x.IsStreaming).IsModified = true;
						var turn = db.ConversationTurns.First(t => t.Id == dbTurn!.Id);
						turn.LastUpdated = DateTime.UtcNow;
						db.SaveChanges();
						currentAssistantMessageId = null;
						currentAssistantContent.Clear();
					}
				}
				else if (e.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
				{
					// Sanitize tool output to ensure no sandbox: URLs are persisted or broadcast
					var sanitizedContent = ConvertSandboxUrlsToPublished(
						e.Message ?? string.Empty,
						dbConversation!.Notebook.ProjectId,
						dbConversation.NotebookId,
						dbConversation.Id,
						publisherId,
						hostUrl);

					using var scope = _scopeFactory.CreateScope();
					var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
					var toolMessage = new NotebookConversationMessage
					{
						NotebookConversationId = dbConversation!.Id,
						TurnIndex = dbTurn!.TurnIndex,
						MessageSequence = currentMessageSequence++,
						Role = DataModelChatRole.Tool,
						Content = sanitizedContent,
						ToolCallId = e.ToolCallId,
						FunctionName = e.FunctionName,
						IsStreaming = false,
						Created = DateTime.UtcNow
					};
					db.NotebookConversationMessages.Add(toolMessage);
					var turn = db.ConversationTurns.First(t => t.Id == dbTurn!.Id);
					turn.LastUpdated = DateTime.UtcNow;
					db.SaveChanges();

					// Update filename -> published URL map based on tool output
					try
					{
						var newMappings = ExtractFilenameToRelativePathMapFromToolMessage(sanitizedContent, hostUrl);
						foreach (var kvp in newMappings)
						{
							var relativePath = kvp.Value;
							
							// If the path doesn't include Runs/ or Output/ prefix, it's a CWD-relative path
							// that needs to be looked up in the database to get the actual notebook-relative path
							if (!relativePath.StartsWith("Runs/", StringComparison.OrdinalIgnoreCase) &&
								!relativePath.StartsWith("Output/", StringComparison.OrdinalIgnoreCase) &&
								!relativePath.Contains("/"))
							{
								// Look up the file by filename in the database - it may be in a Runs folder
								var dbFile = db.NotebookFiles
									.AsNoTracking()
									.Where(f => f.NotebookId == dbConversation.NotebookId)
									.Where(f => f.RelativePath.EndsWith("/" + kvp.Key) || f.RelativePath == kvp.Key)
									.OrderByDescending(f => f.LastModifiedUtc) // Get most recently modified if multiple
									.FirstOrDefault();
								
								if (dbFile != null)
								{
									relativePath = dbFile.RelativePath;
								}
							}
							
							var publishedUrl = BuildPublishedFileUrl(
								hostUrl,
								dbConversation.Notebook.ProjectId,
								dbConversation.NotebookId,
								dbConversation.Id,
								relativePath,
								publisherId);
							filenameToPublishedUrl[kvp.Key] = publishedUrl;
						}
					}
					catch { /* best-effort */ }
				}

				// Emit to stream (minimal client expects 'message' for assistant final content)
				var eventType = e.Role!.Equals("assistant", StringComparison.OrdinalIgnoreCase)
					? StreamingEventTypes.Message
					: DetermineEventType(e.Role!, e.Message!);
				
				string payloadContent;
				if (e.Role!.Equals("assistant", StringComparison.OrdinalIgnoreCase))
				{
					payloadContent = SanitizePublishedAssistantContent(
						e.Message ?? string.Empty,
						filenameToPublishedUrl,
						dbConversation!.Notebook.ProjectId,
						dbConversation.NotebookId,
						dbConversation.Id,
						publisherId,
						hostUrl);
				}
				else if (e.Role!.Equals("tool", StringComparison.OrdinalIgnoreCase))
				{
					payloadContent = ConvertSandboxUrlsToPublished(
						e.Message ?? string.Empty,
						dbConversation!.Notebook.ProjectId,
						dbConversation.NotebookId,
						dbConversation.Id,
						publisherId,
						hostUrl);
				}
				else
				{
					payloadContent = e.Message ?? string.Empty;
				}
				var payload = new { role = e.Role!.ToLowerInvariant(), content = payloadContent, timestamp = DateTime.UtcNow };
				var ev = new StreamingEvent(eventType, JsonSerializer.Serialize(payload, _json));
				try { writer.TryWrite(ev); } catch { }
			};

			// Build invocation context explicitly for published runs.
			var ctx = new AntRunner.ToolCalling.InvocationContext(
				ProjectId: dbConversation.Notebook.ProjectId,
				NotebookId: dbConversation.NotebookId,
				ConversationId: dbConversation.Id,
				OAuthUserAccessToken: chatOptions.oAuthUserAccessToken);

			// Kick off the run (inline streaming): start the run task first
			var httpClient = _httpClientFactory.CreateClient();
            var runTask = AntRunner.Chat.ThreadRun.ExecuteAsync(
				chatOptions,
				_chatClientFactory,
				previousMessages,
				httpClient,
                onMessageAdded,
                onProgress,
                onExternalToolCall: (_, evt) =>
                {
                    // Forward client-handled subset to stream consumers
                    try
                    {
                        var payload = new { toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(evt.ToolCallsJson, _json) };
                        writer.TryWrite(new StreamingEvent(StreamingEventTypes.ExternalToolCall, JsonSerializer.Serialize(payload, _json)));
                    }
                    catch
                    {
                        writer.TryWrite(new StreamingEvent(StreamingEventTypes.ExternalToolCall, evt.ToolCallsJson));
                    }
                },
                resumeWithoutNewUserMessage: false,
				ctx,
				cancellationToken,
				isAgentInvocation: false);

			// When run completes, finalize turn, emit usage, and complete the writer
			_ = Task.Run(async () =>
			{
				try
				{
					var output = await runTask;

                    // Pending client tool: pause stream, usage will be recorded on resume completion
                    if (output?.Status != null && output.Status.Equals("pending_client_tool", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var scope = _scopeFactory.CreateScope())
                        {
                            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            var turn = db.ConversationTurns.First(tx => tx.Id == dbTurn!.Id);
                            // Keep status as 'streaming' while awaiting client tool
                            if (!string.Equals(turn.Status, "streaming", StringComparison.OrdinalIgnoreCase))
                            {
                                turn.Status = "streaming";
                            }
                            turn.LastUpdated = DateTime.UtcNow;
                            db.SaveChanges();
                        }

                        writer.TryWrite(new StreamingEvent(StreamingEventTypes.PendingClientTool, "{}"));
                        return;
                    }

					using (var scope = _scopeFactory.CreateScope())
					{
						var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
						var turn = db.ConversationTurns.First(tx => tx.Id == dbTurn!.Id);
						turn.Status = "completed";
						turn.LastUpdated = DateTime.UtcNow;
						turn.ChatRunOutputJson = output != null ? JsonSerializer.Serialize(output, _json) : null;
						turn.UsageJson = output?.Usage != null ? JsonSerializer.Serialize(output.Usage, _json) : null;
						
						// Store file changes tracked during tool execution
						if (output?.NewFiles != null && output.NewFiles.Count > 0)
						{
							turn.FilesCreated = JsonSerializer.Serialize(output.NewFiles, _json);
						}
						if (output?.ModifiedFiles != null && output.ModifiedFiles.Count > 0)
						{
							turn.FilesModified = JsonSerializer.Serialize(output.ModifiedFiles, _json);
						}
						
						db.SaveChanges();
					}

					await PersistThinkingBlocksAsync(output, assistantMessageIds, CancellationToken.None);
					if (!thinkingEmittedInStream)
					{
						EmitThinkingMessages(output, assistantMessageIds, writer);
					}

					if (output?.Usage != null)
					{
						try
						{
							var usagePayload = new { promptTokens = output.Usage.PromptTokens, completionTokens = output.Usage.CompletionTokens, totalTokens = output.Usage.TotalTokens };
							writer.TryWrite(new StreamingEvent(StreamingEventTypes.Usage, JsonSerializer.Serialize(usagePayload, _json)));

							var cached = output.Usage.CachedPromptTokens ?? 0;
							var prompt = output.Usage.PromptTokens ?? 0;
							var completion = output.Usage.CompletionTokens ?? 0;
							var metrics = new UsageMetrics(prompt, cached, 0, completion);
							var usageService = LlmProviderResolver.ResolveUsageServiceName(modelDeploymentId, _scopeFactory);

							// Attribute usage to the underlying guide/assistant for parity with regular conversations.
							Guid? assistantId = dbConversation.Notebook.GuideId ?? dbConversation.Notebook.NotebookTemplateId;

							// Link to the last assistant message in this conversation, if any.
							Guid? lastAssistantMessageId = null;
							using (var scopeUsage = _scopeFactory.CreateScope())
							{
								var dbUsage = scopeUsage.ServiceProvider.GetRequiredService<ApplicationDbContext>();
								lastAssistantMessageId = await dbUsage.NotebookConversationMessages
									.Where(m => m.NotebookConversationId == dbConversation.Id && m.Role == DataModelChatRole.Assistant)
									.OrderByDescending(m => m.Created)
									.Select(m => (Guid?)m.Id)
									.FirstOrDefaultAsync(CancellationToken.None);
							}

							await _usageRecorder.RecordChatAsync(
								projectId: dbConversation.Notebook.ProjectId,
								notebookId: dbConversation.NotebookId,
								service: usageService,
								modelDeploymentId: modelDeploymentId ?? string.Empty,
								metrics: metrics,
								conversationId: dbConversation.Id,
								metadataJson: null,
								assistantId: assistantId,
								notebookConversationMessageId: lastAssistantMessageId,
								ct: CancellationToken.None);
						}
						catch { /* best-effort */ }
					}

					// Record tool call usage for all tool messages in this turn (query DB like ConversationService)
					// Skip any already recorded (handles edge cases with multiple resume cycles)
					try
					{
						using var scopeTools = _scopeFactory.CreateScope();
						var dbTools = scopeTools.ServiceProvider.GetRequiredService<ApplicationDbContext>();

						// Attribute usage to the underlying guide/assistant for parity with regular conversations.
						Guid? assistantIdForTools = dbConversation.Notebook.GuideId ?? dbConversation.Notebook.NotebookTemplateId;

						var toolMessages = await dbTools.NotebookConversationMessages
							.Where(m => m.NotebookConversationId == dbConversation.Id
									 && m.TurnIndex == dbTurn!.TurnIndex
									 && m.Role == DataModelChatRole.Tool
									 && m.FunctionName != null)
							.Select(m => new { m.Id, m.FunctionName, m.ToolCallId, ContentLength = m.Content != null ? m.Content.Length : 0 })
							.ToListAsync(CancellationToken.None);

						// Get IDs of tool messages that already have usage events recorded
						var toolMessageIds = toolMessages.Select(m => m.Id).ToList();
						var alreadyRecordedIds = await dbTools.UsageEvents
							.Where(u => u.NotebookConversationMessageId != null
									 && toolMessageIds.Contains(u.NotebookConversationMessageId.Value)
									 && u.Category == GuideAntsApi.DataModel.Models.UsageCategory.ToolCall)
							.Select(u => u.NotebookConversationMessageId!.Value)
							.ToListAsync(CancellationToken.None);
						var alreadyRecordedSet = new HashSet<Guid>(alreadyRecordedIds);

						foreach (var toolMsg in toolMessages)
						{
							// Skip crew bridge invocations - they record their own usage
							if (string.Equals(toolMsg.FunctionName, "InvokeAgent", StringComparison.OrdinalIgnoreCase))
								continue;

							// Skip if already recorded
							if (alreadyRecordedSet.Contains(toolMsg.Id))
								continue;

							await _usageRecorder.RecordToolCallAsync(
								projectId: dbConversation.Notebook.ProjectId,
								notebookId: dbConversation.NotebookId,
								conversationId: dbConversation.Id,
								functionName: toolMsg.FunctionName!,
								metadataJson: JsonSerializer.Serialize(new
								{
									toolCallId = toolMsg.ToolCallId,
									functionName = toolMsg.FunctionName,
									contentLength = toolMsg.ContentLength
								}),
								assistantId: assistantIdForTools,
								notebookConversationMessageId: toolMsg.Id,
								ct: CancellationToken.None);
						}
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Failed to record tool call usage for turn {TurnIndex}", dbTurn!.TurnIndex);
					}

					var turnReportedFileChanges =
						(output?.NewFiles?.Count > 0) ||
						(output?.ModifiedFiles?.Count > 0);

					if (_notebookFileSyncService != null && dbConversation.Notebook != null && turnReportedFileChanges)
					{
						try { await _notebookFileSyncService.QueueNotebookSyncAsync(dbConversation.Notebook.Id, CancellationToken.None); }
						catch (Exception ex) { _logger.LogWarning(ex, "Failed to queue notebook sync after published turn"); }
					}

					writer.TryWrite(new StreamingEvent(StreamingEventTypes.Complete, "{}"));
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Published conversation stream failed for conversation {ConversationId}", dbConversation?.Id);
					writer.TryWrite(new StreamingEvent(
						StreamingEventTypes.Error,
						JsonSerializer.Serialize(StreamingErrorEnvelope.Build(ex), _json)));
				}
				finally
				{
					writer.Complete();
				}
			});
		}
		finally
		{
			// Do not dispose throttler or complete the writer here – the background
			// producer owns their lifecycle to avoid ObjectDisposedException during streaming.
		}

		await foreach (var ev in channel.Reader.ReadAllAsync(cancellationToken))
		{
			yield return ev;
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

	// ---- Published URL rewriting helpers (isolated to published flow) ----

	/// <summary>
	/// Converts sandbox:/ URLs to published API URLs.
	/// LLMs sometimes generate sandbox:/path/to/file URLs for new files created in the container.
	/// These need to be converted to published API URLs.
	/// </summary>
	private static string ConvertSandboxUrlsToPublished(
		string content,
		Guid projectId,
		Guid notebookId,
		Guid conversationId,
		string? publisherId,
		string? hostUrl)
	{
		if (string.IsNullOrEmpty(content) || !content.Contains("sandbox:", StringComparison.OrdinalIgnoreCase))
		{
			return content;
		}

		// Pattern to match sandbox:/ URLs in markdown links/images: [text](sandbox:/...) or ![alt](sandbox:/...)
		var mdPattern = new Regex(
			@"(?<head>!?\[[^\]]*\]\()sandbox:/(?<path>[^)]+)(?<tail>\))",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		var result = mdPattern.Replace(content, m =>
		{
			var path = m.Groups["path"].Value;
			var relativePath = NormalizeSandboxPath(path);
			var publishedUrl = BuildPublishedFileUrl(hostUrl, projectId, notebookId, conversationId, relativePath, publisherId);
			return m.Groups["head"].Value + publishedUrl + m.Groups["tail"].Value;
		});

		// Pattern to match sandbox:/ URLs in HTML attributes: href="sandbox:/..." or src="sandbox:/..."
		var htmlDqPattern = new Regex(
			@"(?<attr>href|src)\s*=\s*""sandbox:/(?<path>[^""]+)""",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		result = htmlDqPattern.Replace(result, m =>
		{
			var path = m.Groups["path"].Value;
			var relativePath = NormalizeSandboxPath(path);
			var publishedUrl = BuildPublishedFileUrl(hostUrl, projectId, notebookId, conversationId, relativePath, publisherId);
			return $"{m.Groups["attr"].Value}=\"{publishedUrl}\"";
		});

		// Pattern for single-quoted HTML attributes
		var htmlSqPattern = new Regex(
			@"(?<attr>href|src)\s*=\s*'(?<url>[^']+)'",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		result = htmlSqPattern.Replace(result, m =>
		{
			var path = m.Groups["path"].Value;
			var relativePath = NormalizeSandboxPath(path);
			var publishedUrl = BuildPublishedFileUrl(hostUrl, projectId, notebookId, conversationId, relativePath, publisherId);
			return $"{m.Groups["attr"].Value}='{publishedUrl}'";
		});

		// Final Sweep: Replace any remaining sandbox:/... with relative paths (e.g. in link text, plain text, JSON)
		// This ensures no sandbox:/ URLs are ever leaked to the client/browser.
		// We use relative paths ./foo.png for these to be readable and potentially clickable if the client supports it,
		// or just as a safe fallback for display text.
		var cleanupPattern = new Regex(
			@"sandbox:/(?<path>[^\])""'\s<>]+)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		result = cleanupPattern.Replace(result, m =>
		{
			var path = m.Groups["path"].Value;
			// For display/text purposes, we use the relative path
			return NormalizeSandboxPath(path);
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

		// Return the normalized path (no leading ./ for published URLs)
		return string.IsNullOrEmpty(path) ? "" : path;
	}

	private static Dictionary<string, string> ExtractFilenameToRelativePathMapFromToolMessage(string toolMessageContent, string? hostUrl)
	{
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(toolMessageContent)) return map;

		string textToScan = toolMessageContent;
		try
		{
			using var doc = JsonDocument.Parse(toolMessageContent);
			if (doc.RootElement.ValueKind == JsonValueKind.Object)
			{
				if (doc.RootElement.TryGetProperty("StandardOutput", out var so) && so.ValueKind == JsonValueKind.String)
				{
					textToScan = so.GetString() ?? toolMessageContent;
				}
				
				// Also extract filenames from the JSON NewFiles array
				// These are CWD-relative paths that need DB lookup to get notebook-relative paths
				if (doc.RootElement.TryGetProperty("NewFiles", out var newFilesElement) && newFilesElement.ValueKind == JsonValueKind.Array)
				{
					foreach (var fileElement in newFilesElement.EnumerateArray())
					{
						if (fileElement.ValueKind == JsonValueKind.String)
						{
							var filePath = fileElement.GetString();
							if (!string.IsNullOrWhiteSpace(filePath))
							{
								var normalizedPath = filePath.Replace("\\", "/");
								var filename = Path.GetFileName(normalizedPath);
								if (!string.IsNullOrWhiteSpace(filename) && !map.ContainsKey(filename))
								{
									// Store the path - if it's just a filename, it will need DB lookup
									map[filename] = normalizedPath;
								}
							}
						}
					}
				}
				
				// Also check ModifiedFiles array
				if (doc.RootElement.TryGetProperty("ModifiedFiles", out var modFilesElement) && modFilesElement.ValueKind == JsonValueKind.Array)
				{
					foreach (var fileElement in modFilesElement.EnumerateArray())
					{
						if (fileElement.ValueKind == JsonValueKind.String)
						{
							var filePath = fileElement.GetString();
							if (!string.IsNullOrWhiteSpace(filePath))
							{
								var normalizedPath = filePath.Replace("\\", "/");
								var filename = Path.GetFileName(normalizedPath);
								if (!string.IsNullOrWhiteSpace(filename) && !map.ContainsKey(filename))
								{
									map[filename] = normalizedPath;
								}
							}
						}
					}
				}
			}
		}
		catch { }

		var lines = textToScan.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
		var normalizedHost = string.IsNullOrWhiteSpace(hostUrl) ? null : new Uri(hostUrl).GetLeftPart(UriPartial.Authority).TrimEnd('/');
		for (int i = 0; i < lines.Length; i++)
		{
			var header = lines[i].Trim();
			var isNew = header.Equals("New Files", StringComparison.OrdinalIgnoreCase);
			var isModified = header.Equals("Modified Files", StringComparison.OrdinalIgnoreCase);
			if (!isNew && !isModified) continue;

			int j = i + 1;
			while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j])) j++;
			if (j < lines.Length && lines[j].Trim().Equals("---", StringComparison.Ordinal)) j++;

			for (; j < lines.Length; j++)
			{
				var line = lines[j].Trim();
				if (string.IsNullOrWhiteSpace(line)) break;

				// Tool may emit absolute URLs or "File: relative/path"
				if (!string.IsNullOrWhiteSpace(normalizedHost) && line.StartsWith(normalizedHost, StringComparison.OrdinalIgnoreCase))
				{
					if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) continue;
					string? relPath = null;
					var query = uri.Query.TrimStart('?');
					foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
					{
						var idx = pair.IndexOf('=');
						if (idx <= 0) continue;
						var key = pair.Substring(0, idx);
						if (!key.Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
						var value = Uri.UnescapeDataString(pair.Substring(idx + 1));
						relPath = value.Replace("\\", "/");
						break;
					}
					if (relPath != null)
					{
						var filename = Path.GetFileName(relPath);
						if (!string.IsNullOrWhiteSpace(filename)) map[filename] = relPath;
					}
				}
				else if (line.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
				{
					var rel = line.Substring("File:".Length).Trim().Replace("\\", "/");
					if (!string.IsNullOrWhiteSpace(rel))
					{
						var filename = Path.GetFileName(rel);
						if (!string.IsNullOrWhiteSpace(filename)) map[filename] = rel;
					}
				}
			}
		}

		return map;
	}

	private static string BuildPublishedFileUrl(string? hostUrl, Guid projectId, Guid notebookId, Guid conversationId, string relativePath, string? pubId)
	{
		var pathEncoded = Uri.EscapeDataString(relativePath);
		var basePath = $"/api/published/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/files/content?path={pathEncoded}";
		if (!string.IsNullOrWhiteSpace(pubId))
		{
			basePath += $"&pubId={Uri.EscapeDataString(pubId)}";
		}

		if (!string.IsNullOrWhiteSpace(hostUrl))
		{
			var trimmed = hostUrl!.TrimEnd('/');
			return $"{trimmed}{basePath}";
		}

		// No host configured: return path rooted at /api so client can resolve with api base
		return basePath;
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
			var separator = url.Contains("?") ? "&" : "?";
			return url + separator + key + "=" + Uri.EscapeDataString(value);
		}
	}

	private static string SanitizePublishedAssistantContent(
		string content,
		IDictionary<string, string> filenameToPublishedUrl,
		Guid projectId,
		Guid notebookId,
		Guid conversationId,
		string? publisherId,
		string? hostUrl)
	{
		if (string.IsNullOrEmpty(content))
		{
			return content;
		}

		// First, convert any sandbox:/ URLs to published URLs (LLM sometimes generates these for new files)
		string result = ConvertSandboxUrlsToPublished(content, projectId, notebookId, conversationId, publisherId, hostUrl);

		if (filenameToPublishedUrl.Count == 0)
		{
			// Even if we don't have filename mappings, convert any absolute notebook file URLs to published equivalents
			return ConvertNotebookFileUrlsToPublished(result, projectId, notebookId, conversationId, publisherId, hostUrl);
		}
		var messageStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

		foreach (var kv in filenameToPublishedUrl)
		{
			var filename = kv.Key;
			var basePublishedUrl = kv.Value;
			var withStamp = AppendQueryParamIfMissing(basePublishedUrl, "m", messageStamp);

			// Markdown [text](url) or ![alt](url)
			var mdPattern = new Regex(@"(?<head>(!\[[^\]]*\]|\[[^\]]*\])\()(?<url>[^)]+)(?<tail>\))", RegexOptions.Compiled);
			result = mdPattern.Replace(result, m =>
			{
				var url = m.Groups["url"].Value;
				return url.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0 && !url.Equals(withStamp, StringComparison.OrdinalIgnoreCase)
					? m.Groups["head"].Value + withStamp + m.Groups["tail"].Value
					: m.Value;
			});

			// HTML href="..." or src="..."
			var htmlAttrPattern = new Regex(@"(?<attr>href|src)\s*=\s*""(?<url>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
			result = htmlAttrPattern.Replace(result, m =>
			{
				var url = m.Groups["url"].Value;
				return url.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0 && !url.Equals(withStamp, StringComparison.OrdinalIgnoreCase)
					? m.Value.Replace(url, withStamp)
					: m.Value;
			});

			// HTML href='...' or src='...'
			var htmlAttrPatternSingle = new Regex(@"(?<attr>href|src)\s*=\s*'(?<url>[^']+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
			result = htmlAttrPatternSingle.Replace(result, m =>
			{
				var url = m.Groups["url"].Value;
				return url.IndexOf(filename, StringComparison.OrdinalIgnoreCase) >= 0 && !url.Equals(withStamp, StringComparison.OrdinalIgnoreCase)
					? m.Value.Replace(url, withStamp)
					: m.Value;
			});
		}

		// Also run a general conversion for any notebook file URLs that weren't covered by filename matching
		result = ConvertNotebookFileUrlsToPublished(result, projectId, notebookId, conversationId, publisherId, hostUrl);

		return result;
	}

	private async Task<string?> GetTemplateDefaultModelAsync(Guid templateId, CancellationToken ct)
	{
		try
		{
			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
			var guide = await db.Assistants
				.AsNoTracking()
				.Include(a => a.Model)
				.Where(a => a.Id == templateId && a.Kind == AssistantKind.Guide && a.IsActive)
				.FirstOrDefaultAsync(ct);
			return guide?.Model!.ModelId;
		}
		catch
		{
			return null;
		}
	}

	// Fallback/general conversion: rewrite any notebook file content URLs to published endpoint
	private static string ConvertNotebookFileUrlsToPublished(
		string markdownContent,
		Guid projectId,
		Guid notebookId,
		Guid conversationId,
		string? publisherId,
		string? hostUrl)
	{
		if (string.IsNullOrEmpty(markdownContent)) return markdownContent;

		// Matches markdown links/images, captures the URL
		var mdPattern = new Regex(@"(?<head>(!\[[^\]]*\]|\[[^\]]*\])\()(?<url>[^)]+)(?<tail>\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		// Matches HTML href/src (double-quoted)
		var htmlDqPattern = new Regex(@"(?<attr>href|src)\s*=\s*""(?<url>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		// Matches HTML href/src (single-quoted)
		var htmlSqPattern = new Regex(@"(?<attr>href|src)\s*=\s*'(?<url>[^']+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		Func<string, string> rewriter = (originalUrl) =>
		{
			// Only rewrite notebook file content URLs for this project/notebook
			// Accept both with or without leading /api
			try
			{
				var u = new Uri(originalUrl, UriKind.RelativeOrAbsolute);
				string path = u.IsAbsoluteUri ? u.AbsolutePath : originalUrl;
				string query = u.IsAbsoluteUri ? u.Query : (originalUrl.Contains("?") ? originalUrl.Substring(originalUrl.IndexOf("?", StringComparison.Ordinal)) : "");

				// Allow relative '/api/...' also
				var normalizedPath = path;
				if (!normalizedPath.StartsWith("/"))
				{
					// Not a recognizable path - skip
					return originalUrl;
				}

				// Accept '/api/projects/.../notebooks/.../files/content' or '/projects/.../notebooks/.../files/content'
				var apiPrefix = normalizedPath.StartsWith("/api/") ? "/api" : "";
				var expectedPrefix = $"{apiPrefix}/projects/{projectId}/notebooks/{notebookId}/files/content";
				if (!normalizedPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
				{
					return originalUrl;
				}

				// Extract path and optional 'm' from query
				string? relativeFilePath = null;
				string? mValue = null;
				if (!string.IsNullOrEmpty(query))
				{
					var trimmed = query.TrimStart('?');
					foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
					{
						var idx = part.IndexOf('=');
						if (idx > 0)
						{
							var key = part.Substring(0, idx);
							var val = Uri.UnescapeDataString(part.Substring(idx + 1));
							if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
							{
								relativeFilePath = val.Replace("\\", "/");
							}
							else if (key.Equals("m", StringComparison.OrdinalIgnoreCase))
							{
								mValue = val;
							}
						}
					}
				}
				if (string.IsNullOrWhiteSpace(relativeFilePath))
				{
					return originalUrl;
				}

				// Build published URL
				var publishedBase = BuildPublishedFileUrl(hostUrl, projectId, notebookId, conversationId, relativeFilePath!, publisherId);
				if (!string.IsNullOrWhiteSpace(mValue))
				{
					publishedBase = AppendQueryParamIfMissing(publishedBase, "m", mValue!);
				}
				return publishedBase;
			}
			catch
			{
				return originalUrl;
			}
		};

		var updated = mdPattern.Replace(markdownContent, m =>
		{
			var url = m.Groups["url"].Value;
			var rewritten = rewriter(url);
			return m.Groups["head"].Value + rewritten + m.Groups["tail"].Value;
		});

		updated = htmlDqPattern.Replace(updated, m =>
		{
			var url = m.Groups["url"].Value;
			var rewritten = rewriter(url);
			return m.Value.Replace(url, rewritten);
		});

		updated = htmlSqPattern.Replace(updated, m =>
		{
			var url = m.Groups["url"].Value;
			var rewritten = rewriter(url);
			return m.Value.Replace(url, rewritten);
		});

		return updated;
	}

	private async Task<List<ChatMessage>> BuildMessagesForAssistantAsync(NotebookConversation conv, string assistantName, string? clientContext, CancellationToken ct)
	{
		var list = new List<ChatMessage>();

		// Assistant definition system messages
		try
		{
			var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName);
			if (assistantDef != null)
			{
				// 1. Assistant instructions (primary system prompt)
				if (!string.IsNullOrWhiteSpace(assistantDef.Instructions))
				{
					list.Add(new ChatMessage(ChatMessageRole.System, assistantDef.Instructions));
				}

				// 2. Client-provided context (if any)
				if (!string.IsNullOrWhiteSpace(clientContext))
				{
					list.Add(new ChatMessage(ChatMessageRole.System, clientContext));
				}

				// 3. Server-side context options come LAST to separate them clearly
				var serverContextMsg = _contextOptionsService.BuildPublishedContextMessage(
					assistantDef,
					conv.Notebook?.ProjectId ?? Guid.Empty,
					conv.Notebook?.Id ?? Guid.Empty);
				if (!string.IsNullOrWhiteSpace(serverContextMsg))
				{
					list.Add(new ChatMessage(ChatMessageRole.System, serverContextMsg));
				}
			}
		}
		catch { /* ignore */ }

		// Add conversation history with attachment support
		using (var scope = _scopeFactory.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
			// Pre-index tool messages by toolCallId so we can place them immediately after their assistant tool_calls
			var toolById = conv.Messages
				.Where(x => x.Role == DataModelChatRole.Tool && x.ToolCallId != null && x.FunctionName != null)
				.ToDictionary(x => x.ToolCallId!, x => x, StringComparer.OrdinalIgnoreCase);
			var emittedToolCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var m in conv.Messages.OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence))
			{
				// Tool response filtering: include only tool messages that belong to included assistant messages
				// For simplicity, include all user/assistant messages and tool messages with IDs.
				List<MessageAttachment> attachments = await db.MessageAttachments
					.Include(ma => ma.NotebookFile)
					.Where(ma => ma.MessageId == m.Id)
					.OrderBy(ma => ma.OrderIndex)
					.ToListAsync(ct);

				if (attachments.Count > 0)
				{
					var contents = new List<ChatContent>();
					if (!string.IsNullOrEmpty(m.Content)) contents.Add(new ChatContent(m.Content));
					foreach (var a in attachments)
					{
						var fileContents = await CreateOpenAiContentFromNotebookFileAsync(db, a.NotebookFileId, ct);
						contents.AddRange(fileContents);
					}
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
					list.Add(new ChatMessage(role, contents, null, thinkingBlocks));
					continue;
				}

				// Assistant with tool calls
				if (m.Role == DataModelChatRole.Assistant)
				{
					var thinkingBlocks = DeserializeThinkingBlocks(m.ThinkingBlocksJson);
					var assistantContent = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
					ChatMessage? msg = new ChatMessage(
						ChatMessageRole.Assistant,
						assistantContent,
						null,
						thinkingBlocks);
					if (!string.IsNullOrEmpty(m.ToolCalls))
					{
						try
						{
							var tc = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls, _json);
							if (tc != null)
							{
								msg = new ChatMessage(
									ChatMessageRole.Assistant,
									assistantContent,
									tc,
									thinkingBlocks);

								// Immediately append matching tool messages in the correct order
								foreach (var call in tc)
								{
									if (string.IsNullOrEmpty(call.Id)) continue;
									if (emittedToolCallIds.Contains(call.Id)) continue;
									if (toolById.TryGetValue(call.Id, out var toolMsg))
									{
										var toolContent = string.IsNullOrEmpty(toolMsg.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(toolMsg.Content) };
										if (msg != null)
										{
											list.Add(msg);
											msg = null; // prevent duplicate add below
										}
										list.Add(new ChatMessage(toolMsg.ToolCallId!, toolMsg.FunctionName!, toolContent));
										emittedToolCallIds.Add(call.Id);
									}
								}
							}
						}
						catch { /* ignore */ }
					}
					// If not already added above, add now
					if (msg != null) list.Add(msg);
					continue;
				}

				if (m.Role == DataModelChatRole.Tool && m.ToolCallId != null && m.FunctionName != null)
				{
					// Skip if we already emitted this tool message right after its assistant tool_calls
					if (emittedToolCallIds.Contains(m.ToolCallId)) continue;
					var toolMsgContent = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
					list.Add(new ChatMessage(m.ToolCallId, m.FunctionName, toolMsgContent));
					continue;
				}

				var basicRole = m.Role switch
				{
					DataModelChatRole.User => ChatMessageRole.User,
					DataModelChatRole.Assistant => ChatMessageRole.Assistant,
					DataModelChatRole.Tool => ChatMessageRole.Tool,
					_ => ChatMessageRole.System
				};
				var basicThinkingBlocks = basicRole == ChatMessageRole.Assistant
					? DeserializeThinkingBlocks(m.ThinkingBlocksJson)
					: null;
				var basicContent = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
				list.Add(new ChatMessage(basicRole, basicContent, null, basicThinkingBlocks));
			}
		}

		return list;
	}

	private static IReadOnlyList<ChatThinkingBlock>? DeserializeThinkingBlocks(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		try
		{
			var blocks = JsonSerializer.Deserialize<List<ChatThinkingBlock>>(json, _json);
			return blocks is { Count: > 0 } ? blocks : null;
		}
		catch (JsonException ex)
		{
			System.Diagnostics.Debug.WriteLine($"Failed to deserialize thinking blocks: {ex.Message}");
			return null;
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

			var json = JsonSerializer.Serialize(thinkingBlocks, _json);
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
				writer.TryWrite(new StreamingEvent("assistant_message", JsonSerializer.Serialize(payload, _json)));
			}
		}
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

	private async Task AddAttachmentsToUserMessageAsync(ApplicationDbContext db, Guid userMessageId, Guid notebookId, IReadOnlyList<AttachmentDto> attachments, CancellationToken ct)
	{
		for (int i = 0; i < attachments.Count; i++)
		{
			var attachment = attachments[i];
			var notebookFile = await db.NotebookFiles
				.FirstOrDefaultAsync(f => f.Id == attachment.NotebookFileId && f.NotebookId == notebookId, ct);
			if (notebookFile == null) continue;
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
		await db.SaveChangesAsync(ct);
	}

	private async Task<List<ChatMessage>> CreateOpenAiMessagesFromNotebookFileAsync(ApplicationDbContext db, Guid notebookFileId, CancellationToken ct)
	{
		var messages = new List<ChatMessage>();
		if (_notebookFileService == null) return messages;
		try
		{
			var notebookFile = await db.NotebookFiles
				.Include(nf => nf.Notebook)
				.FirstOrDefaultAsync(nf => nf.Id == notebookFileId, ct);
			if (notebookFile == null) return messages;
			return await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
				notebookFile,
				_notebookFileService,
				null,
				_configuration?["FileStorage:Path"] ?? string.Empty,
				ct,
				_markdownAttachmentOptions.Value.MaxInlineCharacters,
				_logger);
		}
		catch
		{
			return messages;
		}
	}

	private async Task<List<ChatContent>> CreateOpenAiContentFromNotebookFileAsync(ApplicationDbContext db, Guid notebookFileId, CancellationToken ct)
	{
		var contents = new List<ChatContent>();
		if (_notebookFileService == null) return contents;
		try
		{
			var notebookFile = await db.NotebookFiles
				.Include(nf => nf.Notebook)
				.FirstOrDefaultAsync(nf => nf.Id == notebookFileId, ct);
			if (notebookFile == null) return contents;
			return await AttachmentMessageBuilder.CreateContentFromNotebookFileAsync(
				notebookFile,
				_notebookFileService,
				null,
				_configuration?["FileStorage:Path"] ?? string.Empty,
				ct,
				_markdownAttachmentOptions.Value.MaxInlineCharacters,
				_logger);
		}
		catch
		{
			return contents;
		}
	}
}
