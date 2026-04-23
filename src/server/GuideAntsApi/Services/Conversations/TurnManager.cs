using Microsoft.EntityFrameworkCore;
using AntRunner.Chat;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using System.Text.Json;

namespace GuideAntsApi.Services.Conversations;

public class TurnManager : ITurnManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotebookFileService _notebookFileService;
    private readonly IFileLineageService _lineageService;
    private readonly IConversationManager _conversationManager;
    private readonly IChatModelResolver _chatModelResolver;
    private readonly ILogger<TurnManager> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TurnManager(
        IServiceScopeFactory scopeFactory,
        INotebookFileService notebookFileService,
        IFileLineageService lineageService,
        IConversationManager conversationManager,
        IChatModelResolver chatModelResolver,
        ILogger<TurnManager> logger)
    {
        _scopeFactory = scopeFactory;
        _notebookFileService = notebookFileService;
        _lineageService = lineageService;
        _conversationManager = conversationManager;
        _chatModelResolver = chatModelResolver;
        _logger = logger;
    }

    public async Task<Turn> CreateTurnAsync(Guid conversationId, string assistantName, string instructions)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Get the next turn index
        var nextTurnIndex = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId)
            .MaxAsync(t => (int?)t.TurnIndex) ?? 0;
        nextTurnIndex++;

        // Create the Turn object for the Conversation class
        var turn = new Turn
        {
            AssistantName = assistantName,
            Instructions = instructions
        };

        _logger.LogInformation("Created turn {TurnIndex} for conversation {ConversationId} with assistant {AssistantName}", 
            nextTurnIndex, conversationId, assistantName);

        return turn;
    }

    public async Task CompleteTurnAsync(Guid conversationId, Turn turn, ChatRunOutput output)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Get the next turn index
        var nextTurnIndex = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId)
            .MaxAsync(t => (int?)t.TurnIndex) ?? 0;
        nextTurnIndex++;

        var assistantName = turn.AssistantName ?? "assistant";
        var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName);
        var resolved = _chatModelResolver.Resolve(assistantDef?.Model);

        var dbTurn = new ConversationTurn
        {
            NotebookConversationId = conversationId,
            TurnIndex = nextTurnIndex,
            AssistantName = assistantName,
            ModelDeploymentId = resolved.ModelId,
            Instructions = turn.Instructions ?? string.Empty,
            ChatRunOutputJson = JsonSerializer.Serialize(output, _jsonOptions),
            UsageJson = output.Usage != null ? JsonSerializer.Serialize(output.Usage, _jsonOptions) : null,
            Created = DateTime.UtcNow
        };

        db.ConversationTurns.Add(dbTurn);
        await db.SaveChangesAsync();

        // Invalidate conversation cache since we've added a new turn
        _conversationManager.InvalidateCache(conversationId);

        _logger.LogInformation("Completed turn {TurnIndex} for conversation {ConversationId}", 
            nextTurnIndex, conversationId);
    }

    public async Task<List<Turn>> GetTurnsAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var dbTurns = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId)
            .OrderBy(t => t.TurnIndex)
            .ToListAsync();

        var turns = new List<Turn>();

        foreach (var dbTurn in dbTurns)
        {
            var turn = new Turn
            {
                AssistantName = dbTurn.AssistantName,
                Instructions = dbTurn.Instructions
            };

            // Deserialize ChatRunOutput if available
            if (!string.IsNullOrEmpty(dbTurn.ChatRunOutputJson))
            {
                try
                {
                    var property = typeof(Turn).GetProperty("ChatRunOutput");
                    if (property != null)
                    {
                        var output = JsonSerializer.Deserialize<ChatRunOutput>(dbTurn.ChatRunOutputJson, _jsonOptions);
                        property.SetValue(turn, output);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize ChatRunOutput for turn {TurnIndex}", dbTurn.TurnIndex);
                }
            }

            turns.Add(turn);
        }

        return turns;
    }

    public async Task<List<NotebookFileDto>> GetFilesCreatedInTurnAsync(Guid conversationId, int turnIndex)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var turn = await db.ConversationTurns
            .Include(t => t.NotebookConversation)
            .FirstOrDefaultAsync(t => t.NotebookConversationId == conversationId && t.TurnIndex == turnIndex);

        if (turn == null || string.IsNullOrEmpty(turn.FilesCreated))
        {
            return new List<NotebookFileDto>();
        }

        // FilesCreated now contains CWD-relative paths (e.g., "foo.png")
        var filePaths = JsonSerializer.Deserialize<List<string>>(turn.FilesCreated, _jsonOptions) ?? new List<string>();
        var files = new List<NotebookFileDto>();
        var notebookId = turn.NotebookConversation.NotebookId;

        foreach (var cwdPath in filePaths)
        {
            // Look up by path suffix match (CWD-relative paths need to match end of RelativePath)
            var notebookFile = await db.NotebookFiles
                .FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath.EndsWith(cwdPath));
            
            if (notebookFile != null)
            {
                var fileName = Path.GetFileName(notebookFile.RelativePath);
                var dto = new NotebookFileDto(
                    notebookFile.Id,
                    fileName,
                    notebookFile.RelativePath,
                    notebookFile.FileSize,
                    notebookFile.LastModifiedUtc,
                    notebookFile.FileHash,
                    notebookFile.OriginContentFileVersionId,
                    false,
                    false
                );
                files.Add(dto);
            }
        }

        return files;
    }

    public async Task<List<NotebookFileDto>> GetFilesCreatedInConversationAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var conversation = await db.NotebookConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId);
        
        if (conversation == null)
        {
            return new List<NotebookFileDto>();
        }
        
        var turns = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId && t.FilesCreated != null)
            .ToListAsync();

        // Collect unique CWD-relative paths from all turns
        var allFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var turn in turns)
        {
            if (!string.IsNullOrEmpty(turn.FilesCreated))
            {
                try
                {
                    var filePaths = JsonSerializer.Deserialize<List<string>>(turn.FilesCreated, _jsonOptions);
                    if (filePaths != null)
                    {
                        foreach (var path in filePaths)
                        {
                            allFilePaths.Add(path);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize FilesCreated for turn {TurnIndex}", turn.TurnIndex);
                }
            }
        }

        var files = new List<NotebookFileDto>();

        foreach (var cwdPath in allFilePaths)
        {
            // Look up by path suffix match
            var notebookFile = await db.NotebookFiles
                .FirstOrDefaultAsync(f => f.NotebookId == conversation.NotebookId && f.RelativePath.EndsWith(cwdPath));
            
            if (notebookFile != null)
            {
                var fileName = Path.GetFileName(notebookFile.RelativePath);
                var dto = new NotebookFileDto(
                    notebookFile.Id,
                    fileName,
                    notebookFile.RelativePath,
                    notebookFile.FileSize,
                    notebookFile.LastModifiedUtc,
                    notebookFile.FileHash,
                    notebookFile.OriginContentFileVersionId,
                    false,
                    false
                );
                files.Add(dto);
            }
        }

        return files;
    }

    public async Task TrackFilesForTurnAsync(Guid conversationId, int turnIndex, List<string> createdFiles, List<string> modifiedFiles)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var turn = await db.ConversationTurns
            .FirstOrDefaultAsync(t => t.NotebookConversationId == conversationId && t.TurnIndex == turnIndex);

        if (turn == null)
        {
            throw new KeyNotFoundException($"Turn not found: conversation {conversationId}, turn {turnIndex}");
        }

        if (createdFiles?.Any() == true)
        {
            turn.FilesCreated = JsonSerializer.Serialize(createdFiles, _jsonOptions);
        }

        if (modifiedFiles?.Any() == true)
        {
            turn.FilesModified = JsonSerializer.Serialize(modifiedFiles, _jsonOptions);
        }

        await db.SaveChangesAsync();

        _logger.LogInformation("Tracked {CreatedCount} created and {ModifiedCount} modified files for turn {TurnIndex} in conversation {ConversationId}", 
            createdFiles?.Count ?? 0, modifiedFiles?.Count ?? 0, turnIndex, conversationId);
    }

    public async Task<List<FileLineageEventDto>> GetFileOperationsForTurnAsync(Guid conversationId, int turnIndex)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Get the turn timeframe to filter lineage events
        var turn = await db.ConversationTurns
            .FirstOrDefaultAsync(t => t.NotebookConversationId == conversationId && t.TurnIndex == turnIndex);

        if (turn == null)
        {
            return new List<FileLineageEventDto>();
        }

        // Get the conversation to find the notebook
        var conversation = await db.NotebookConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conversation == null)
        {
            return new List<FileLineageEventDto>();
        }

        // Find the next turn to establish the timeframe
        var nextTurn = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId && t.TurnIndex > turnIndex)
            .OrderBy(t => t.TurnIndex)
            .FirstOrDefaultAsync();

        var fromTime = turn.Created;
        var toTime = nextTurn?.Created ?? DateTime.UtcNow;

        // Get lineage events for notebook files during this turn's timeframe
        var events = await db.FileLineageEvents
            .Where(e => e.NotebookId == conversation.NotebookId 
                     && e.Timestamp >= fromTime 
                     && e.Timestamp < toTime
                     && e.FileKind == FileKind.Notebook)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

        return events.Select(e => new FileLineageEventDto(
            e.Id,
            e.Action,
            e.FileKind,
            e.FileId,
            e.VersionNumber,
            e.ProjectId,
            e.NotebookId,
            e.StoragePath,
            e.Timestamp,
            e.UserId,
            e.UserId, // UserDisplayName - using UserId as placeholder
            "Unknown", // FileName - would need to join with NotebookFiles
            "Unknown", // FileVersionLabel - would need additional logic
            null // NotebookName - would need to join with Notebooks
        )).ToList();
    }
}