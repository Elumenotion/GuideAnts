using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Conversations.Mapping;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations.Queries;

public class ConversationQueryService : IConversationQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ConversationQueryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
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
                .ThenInclude(m => m.User)
            .Include(c => c.Messages)
                .ThenInclude(m => m.LastEditedByUser)
            .Include(c => c.Messages)
                .ThenInclude(m => m.Attachments)
                    .ThenInclude(a => a.NotebookFile)
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conv == null) return null;

        return ConversationMessageMapper.ToConversationDto(conv);
    }

    public async Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var projectId = await db.NotebookConversations
            .Where(c => c.Id == conversationId)
            .Select(c => c.Notebook.ProjectId)
            .FirstOrDefaultAsync();

        if (projectId == Guid.Empty) return null;

        if (db.Database.IsRelational())
        {
            // Use READ UNCOMMITTED for this read-only query — prevents blocking by retention cleanup
            // lock escalation on NotebookConversationMessages/ConversationTurns tables.
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED");
        }

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
                        UserName = m.UserId.HasValue
                            ? (m.User != null ? m.User.Name : null)
                            : (m.LastEditedByUser != null ? m.LastEditedByUser.Name : null),
                        UserEmail = m.UserId.HasValue
                            ? (m.User != null ? m.User.Email : null)
                            : (m.LastEditedByUser != null ? m.LastEditedByUser.Email : null),
                        m.AssistantName,
                        m.IsEdited,
                        m.LastEditedAt,
                        m.Created,
                        OriginalContent = m.EditHistory != null ? m.EditHistory.OriginalContent : null,
                        m.ToolCalls,
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
                                FileType = a.NotebookFile != null ? ConversationMessageMapper.DetermineFileTypeString(a.NotebookFile.RelativePath ?? "") : "other",
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

        var turnFilesCreated = new Dictionary<int, List<string>>();
        var turnFilesModified = new Dictionary<int, List<string>>();
        foreach (var turn in conversationData.Turns)
        {
            if (!string.IsNullOrEmpty(turn.FilesCreated))
            {
                try
                {
                    var files = JsonSerializer.Deserialize<List<string>>(turn.FilesCreated, JsonOptions);
                    if (files != null && files.Count > 0)
                        turnFilesCreated[turn.TurnIndex] = files;
                }
                catch { /* ignore parse errors */ }
            }
            if (!string.IsNullOrEmpty(turn.FilesModified))
            {
                try
                {
                    var files = JsonSerializer.Deserialize<List<string>>(turn.FilesModified, JsonOptions);
                    if (files != null && files.Count > 0)
                        turnFilesModified[turn.TurnIndex] = files;
                }
                catch { /* ignore parse errors */ }
            }
        }

        var lastAssistantMessagePerTurn = conversationData.Messages
            .Where(m => m.Role == DataModelChatRole.Assistant)
            .GroupBy(m => m.TurnIndex)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.MessageSequence).First().Id);

        var messageDtos = new List<MessageDto>();
        foreach (var msg in conversationData.Messages)
        {
            messageDtos.AddRange(ConversationMessageMapper.BuildThinkingMessageDtos(
                msg.Id,
                msg.Role,
                msg.AssistantName,
                msg.Created,
                msg.ThinkingBlocksJson,
                msg.TurnIndex));

            IReadOnlyList<ToolCallDto>? toolCalls = null;
            if (!string.IsNullOrEmpty(msg.ToolCalls))
            {
                try
                {
                    var openAiToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(msg.ToolCalls, JsonOptions);
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
                catch { /* leave toolCalls null */ }
            }

            var attachments = msg.Attachments.Select(a => new AttachedFileDto(
                a.NotebookFileId,
                a.FileName,
                a.FileType,
                a.FileSize,
                null,
                a.Type
            )).ToList();

            var isLastAssistantInTurn = lastAssistantMessagePerTurn.TryGetValue(msg.TurnIndex, out var lastAssistantId)
                && lastAssistantId == msg.Id;

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
                null,
                msg.TurnIndex,
                filesCreated,
                filesModified,
                msg.UserName,
                msg.UserEmail
            ));
        }

        var filteredMessageDtos = ConversationMessageMapper.FilterDuplicateAssistantMessages(
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

    public async Task<IReadOnlyList<NotebookConversationListDto>> GetListAsync(Guid notebookId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var projectId = await db.Notebooks
            .Where(n => n.Id == notebookId)
            .Select(n => n.ProjectId)
            .FirstOrDefaultAsync();

        if (projectId == Guid.Empty) return [];

        if (db.Database.IsRelational())
        {
            // Use READ UNCOMMITTED — this read-only list must not be blocked by retention cleanup
            // locks on NotebookConversations or UsageEvents for this notebook.
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED");
        }

        var convs = await db.NotebookConversations
            .Where(c => c.NotebookId == notebookId)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.Created,
                LastActivity = db.UsageEvents
                    .Where(e => e.ConversationId == c.Id)
                    .Max(e => (DateTime?)e.Created) ?? c.Created
            })
            .OrderByDescending(c => c.LastActivity)
            .Select(c => new NotebookConversationListDto(c.Id, c.Title, c.Created, c.LastActivity))
            .ToListAsync();
        return convs;
    }

    public async Task<PagedUserConversationsDto> GetUserConversationsAsync(UserConversationsQuery query)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var currentUserService = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        var currentUser = await currentUserService.GetCurrentUserAsync().ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Authenticated user is required.");

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Max(1, Math.Min(100, query.PageSize));
        var searchTerm = query.Search?.Trim();
        var sortBy = query.SortBy?.ToLower() ?? "date";
        var sortOrder = query.SortOrder?.ToLower() ?? "desc";

        var userConversationIds = db.NotebookConversationMessages
            .Where(m => m.UserId == currentUser.UserId)
            .Select(m => m.NotebookConversationId)
            .Distinct();

        var queryable = db.NotebookConversations
            .Where(c => userConversationIds.Contains(c.Id)
                        && !c.Notebook.Project.Deleted
                        && !c.Notebook.Project.IsSystemProject)
            .Select(c => new
            {
                c.Id,
                c.Title,
                NotebookId = c.NotebookId,
                NotebookTitle = c.Notebook.Title,
                ProjectId = c.Notebook.ProjectId,
                ProjectTitle = c.Notebook.Project.Title,
                c.Created,
                LastActivity = db.NotebookConversationMessages
                    .Where(m => m.NotebookConversationId == c.Id)
                    .Max(m => (DateTime?)m.Created) ?? c.Created
            });

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var searchLower = searchTerm.ToLower();
            queryable = queryable.Where(c =>
                c.Title.ToLower().Contains(searchLower) ||
                c.NotebookTitle.ToLower().Contains(searchLower) ||
                c.ProjectTitle.ToLower().Contains(searchLower)
            );
        }

        queryable = sortBy switch
        {
            "project" => sortOrder == "asc"
                ? queryable.OrderBy(c => c.ProjectTitle).ThenByDescending(c => c.LastActivity)
                : queryable.OrderByDescending(c => c.ProjectTitle).ThenByDescending(c => c.LastActivity),
            "notebook" => sortOrder == "asc"
                ? queryable.OrderBy(c => c.NotebookTitle).ThenByDescending(c => c.LastActivity)
                : queryable.OrderByDescending(c => c.NotebookTitle).ThenByDescending(c => c.LastActivity),
            _ => sortOrder == "asc"
                ? queryable.OrderBy(c => c.LastActivity)
                : queryable.OrderByDescending(c => c.LastActivity)
        };

        var totalCount = await queryable.CountAsync();

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
}
