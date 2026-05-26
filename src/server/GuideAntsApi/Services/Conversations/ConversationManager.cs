using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using AntRunner.Chat;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.Services.Routing;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations;

public class ConversationManager : IConversationManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ConversationManager> _logger;
    private readonly IChatModelResolver _chatModelResolver;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ConversationManager(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<ConversationManager> logger,
        IChatModelResolver chatModelResolver)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
        _chatModelResolver = chatModelResolver ?? throw new ArgumentNullException(nameof(chatModelResolver));
    }

    public async Task<AntRunner.Chat.Conversation> CreateConversationAsync(Guid conversationId, string assistantName)
    {
        var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName)
            ?? throw new ArgumentException($"Assistant definition not found for: {assistantName}");

        var resolved = _chatModelResolver.Resolve(assistantDef.Model);
        var chatOptions = new ChatRunOptions
        {
            AssistantName = assistantName,
            DeploymentId = resolved.ModelId,
            ExecutionPolicy = resolved.ExecutionPolicy
        };

        var conversation = await AntRunner.Chat.Conversation.Create(chatOptions);

        _logger.LogInformation("Created new conversation {ConversationId} with assistant {AssistantName}", 
            conversationId, assistantName);

        return conversation;
    }

    public async Task<AntRunner.Chat.Conversation> LoadConversationAsync(Guid conversationId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dbConversation = await db.NotebookConversations
            .Include(c => c.Messages)
                .ThenInclude(m => m.EditHistory)
            .FirstOrDefaultAsync(c => c.Id == conversationId)
            ?? throw new KeyNotFoundException($"Conversation not found: {conversationId}");

        var currentState = await GetCurrentStateAsync(conversationId);
        var assistantName = currentState.CurrentAssistantName ?? "assistant";

        var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName);
        var resolved = _chatModelResolver.Resolve(assistantDef?.Model);

        var chatOptions = new ChatRunOptions
        {
            AssistantName = assistantName,
            DeploymentId = resolved.ModelId,
            ExecutionPolicy = resolved.ExecutionPolicy
        };

        var conversation = await AntRunner.Chat.Conversation.Create(chatOptions);

        // Load message history
        var chatMessages = dbConversation.Messages
            .OrderBy(m => m.Created)
            .Select(ToChatMessage)
            .ToList();

        if (chatMessages.Any())
        {
            conversation.AssistantMessages[assistantName] = chatMessages;
        }

        _logger.LogInformation("Loaded conversation {ConversationId} with {MessageCount} messages", 
            conversationId, chatMessages.Count);

        return conversation;
    }

    public async Task SaveConversationAsync(AntRunner.Chat.Conversation conversation)
    {
        // Note: In the enhanced architecture, conversation state is primarily maintained
        // in the database through turns and messages. This method could be used for
        // caching or serialization purposes if needed.
        
        _logger.LogInformation("Conversation save requested - state managed through database");
        await Task.CompletedTask;
    }

    public async Task<AntRunner.Chat.Conversation> RestoreConversationStateAsync(Guid conversationId)
    {
        // This method restores a conversation from the database state
        return await LoadConversationAsync(conversationId);
    }

    public async Task<ConversationStateDto> GetCurrentStateAsync(Guid conversationId)
    {
        var cacheKey = $"conversation:{conversationId}:state";
        
        if (_cache.TryGetValue(cacheKey, out ConversationStateDto? cachedState) && cachedState != null)
        {
            return cachedState;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var currentState = await db.ConversationCurrentState
            .Where(c => c.Id == conversationId)
            .FirstOrDefaultAsync();

        ConversationStateDto dto;
        if (currentState == null)
        {
            // In-memory test databases and some providers do not materialize the keyless
            // ConversationCurrentState view; compute the same shape from base tables.
            var computed = await TryGetStateFromBaseTablesAsync(db, conversationId);
            if (computed == null)
            {
                throw new KeyNotFoundException($"Conversation not found: {conversationId}");
            }
            dto = computed;
        }
        else
        {
            dto = new ConversationStateDto
            {
                ConversationId = currentState.Id,
                CurrentAssistantName = currentState.CurrentAssistantName,
                CurrentModelDeploymentId = currentState.CurrentModelDeploymentId,
                LastInstructions = currentState.LastInstructions,
                LastActivity = currentState.LastActivity,
                CurrentTurnIndex = currentState.CurrentTurnIndex ?? 0,
                LastTurnFilesCreated = ParseFilePaths(currentState.LastTurnFilesCreated),
                LastTurnFilesModified = ParseFilePaths(currentState.LastTurnFilesModified)
            };
        }

        // Cache for 5 minutes with size (required when SizeLimit is set)
        _cache.Set(cacheKey, dto, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
            Size = 1
        });

        return dto;
    }

    public async Task<string> GetCurrentAssistantAsync(Guid conversationId)
    {
        var cacheKey = $"conversation:{conversationId}:current-assistant";
        
        if (_cache.TryGetValue(cacheKey, out string? cachedAssistant) && cachedAssistant != null)
        {
            return cachedAssistant;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var assistant = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId)
            .OrderByDescending(t => t.TurnIndex)
            .Select(t => t.AssistantName)
            .FirstOrDefaultAsync();

        if (assistant == null)
        {
            // Default assistant if no turns exist
            assistant = "assistant";
        }

        // Cache for 5 minutes with size (required when SizeLimit is set)
        _cache.Set(cacheKey, assistant, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
            Size = 1
        });

        return assistant;
    }

    public async Task<string> GetCurrentModelAsync(Guid conversationId)
    {
        var state = await GetCurrentStateAsync(conversationId);
        return state.CurrentModelDeploymentId ?? string.Empty;
    }

    public void InvalidateCache(Guid conversationId)
    {
        var keys = new[]
        {
            $"conversation:{conversationId}:state",
            $"conversation:{conversationId}:current-assistant",
            $"conversation:{conversationId}:current-model"
        };

        foreach (var key in keys)
        {
            _cache.Remove(key);
        }

        _logger.LogDebug("Invalidated cache for conversation {ConversationId}", conversationId);
    }

    private async Task<ConversationStateDto?> TryGetStateFromBaseTablesAsync(
        ApplicationDbContext db,
        Guid conversationId)
    {
        var exists = await db.NotebookConversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => c.Id)
            .FirstOrDefaultAsync();
        if (exists == Guid.Empty)
        {
            return null;
        }

        var lastTurn = await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId)
            .OrderByDescending(t => t.TurnIndex)
            .FirstOrDefaultAsync();

        return new ConversationStateDto
        {
            ConversationId = exists,
            CurrentAssistantName = lastTurn?.AssistantName,
            CurrentModelDeploymentId = lastTurn?.ModelDeploymentId,
            LastInstructions = lastTurn?.Instructions,
            LastActivity = lastTurn?.Created,
            CurrentTurnIndex = lastTurn?.TurnIndex ?? 0,
            LastTurnFilesCreated = lastTurn is null
                ? null
                : ParseFilePaths(lastTurn.FilesCreated),
            LastTurnFilesModified = lastTurn is null
                ? null
                : ParseFilePaths(lastTurn.FilesModified)
        };
    }

    private static List<string>? ParseFilePaths(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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
            return new ChatMessage(role, m.Content);
        }

        if (role == ChatMessageRole.Tool && m.ToolCallId != null && m.FunctionName != null)
        {
            var content = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
            return new ChatMessage(m.ToolCallId, m.FunctionName, content);
        }

        return new ChatMessage(role, m.Content);
    }
}
