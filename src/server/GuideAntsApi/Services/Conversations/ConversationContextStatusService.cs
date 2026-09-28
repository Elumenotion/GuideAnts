using System.Text.Json;
using AntRunner.Chat;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.EntityFrameworkCore;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations;

public sealed class ConversationContextStatusService : IConversationContextStatusService
{
    private const int MaxTurnsSampled = 10;

    // Same options ConversationPersistence serializes UsageJson with.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IContextWindowResolver _resolver;
    private readonly IRouterModelsConfigService _routerModels;
    private readonly ILogger<ConversationContextStatusService> _logger;

    public ConversationContextStatusService(
        IServiceScopeFactory scopeFactory,
        IContextWindowResolver resolver,
        IRouterModelsConfigService routerModels,
        ILogger<ConversationContextStatusService> logger)
    {
        _scopeFactory = scopeFactory;
        _resolver = resolver;
        _routerModels = routerModels;
        _logger = logger;
    }

    public async Task<ConversationContextStatusDto> GetAsync(Guid conversationId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var boundaryTurnIndex = await db.NotebookConversations
            .Where(c => c.Id == conversationId)
            .Select(c => c.CompactionBoundaryTurnIndex)
            .FirstOrDefaultAsync(ct);

        var turns = await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId && t.Status == "completed")
            .OrderByDescending(t => t.TurnIndex)
            .Take(MaxTurnsSampled)
            .Select(t => new { t.TurnIndex, t.ModelDeploymentId, t.UsageJson })
            .ToListAsync(ct);

        if (turns.Count == 0)
        {
            return new ConversationContextStatusDto(null, null, boundaryTurnIndex, ContextEstimateSource.None, null, ContextWindowSource.Unknown);
        }

        var modelId = turns[0].ModelDeploymentId;

        var usages = new List<(int TurnIndex, int Tokens, int Chars)>();
        foreach (var turn in turns)
        {
            if (string.IsNullOrWhiteSpace(turn.UsageJson))
            {
                continue;
            }

            try
            {
                var usage = JsonSerializer.Deserialize<UsageResponse>(turn.UsageJson, JsonOptions);
                if (usage?.LastRoundPromptTokens is > 0)
                {
                    usages.Add((turn.TurnIndex, usage.LastRoundPromptTokens.Value, usage.LastRoundPromptChars ?? 0));
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping unparseable UsageJson for conversation {ConversationId}", conversationId);
            }
        }

        var charsPerToken = PromptTokenEstimator.CharsPerToken(usages.Select(u => (u.Tokens, u.Chars)));

        // Lengths only; never materialize message bodies.
        var messages = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId && m.IsStreaming != true)
            .Select(m => new
            {
                m.TurnIndex,
                m.Role,
                m.MessageSequence,
                Len = m.Content == null ? 0 : m.Content.Length
            })
            .ToListAsync(ct);

        int? estimate;
        ContextEstimateSource source;
        if (usages.Count > 0)
        {
            var baseline = usages.OrderByDescending(u => u.TurnIndex).First();
            long trailingChars = messages.Where(m => m.TurnIndex > baseline.TurnIndex).Sum(m => (long)m.Len);
            var finalReply = messages
                .Where(m => m.TurnIndex == baseline.TurnIndex && m.Role == DataModelChatRole.Assistant)
                .OrderByDescending(m => m.MessageSequence)
                .FirstOrDefault();
            trailingChars += finalReply?.Len ?? 0;

            estimate = (int)Math.Min(
                (long)int.MaxValue,
                (long)baseline.Tokens + PromptTokenEstimator.EstimateTokens(ClampToInt(trailingChars), charsPerToken));
            source = ContextEstimateSource.ProviderUsage;
        }
        else
        {
            // Match ConversationHistoryBuilder's own pre-boundary cut: once compacted, only the
            // verbatim tail is what the model actually sees. Summing everything here would show a
            // stale, uncompacted estimate next to a meter that already says "Compacted".
            var postBoundaryChars = boundaryTurnIndex.HasValue
                ? messages.Where(m => m.TurnIndex > boundaryTurnIndex.Value).Sum(m => (long)m.Len)
                : messages.Sum(m => (long)m.Len);
            estimate = PromptTokenEstimator.EstimateTokens(ClampToInt(postBoundaryChars), charsPerToken);
            source = ContextEstimateSource.Characters;
        }

        int? window = null;
        var windowSource = ContextWindowSource.Unknown;
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var live = await TryGetLiveContextSizeAsync(db, modelId, ct);
            var info = _resolver.Resolve(modelId, live);
            window = info.ContextWindowTokens;
            windowSource = info.Source;
        }

        return new ConversationContextStatusDto(window, estimate, boundaryTurnIndex, source, modelId, windowSource);
    }

    private static int ClampToInt(long value) => (int)Math.Min(value, int.MaxValue);

    private async Task<int?> TryGetLiveContextSizeAsync(ApplicationDbContext db, string modelId, CancellationToken ct)
    {
        try
        {
            var row = await db.Models
                .AsNoTracking()
                .Where(m => m.ModelId == modelId)
                .Select(m => new { m.Provider, m.RuntimeConfigJson })
                .FirstOrDefaultAsync(ct);

            if (row == null
                || !string.Equals(row.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(row.RuntimeConfigJson))
            {
                return null;
            }

            var routerModelId = LocalRuntimeConfigurationParser.Parse(modelId, row.RuntimeConfigJson).RouterModelId;
            var entries = await _routerModels.GetEntriesAsync(ct);
            return entries
                .FirstOrDefault(e => string.Equals(e.Alias, routerModelId, StringComparison.OrdinalIgnoreCase))
                ?.ContextSize;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read live context size for model {ModelId}; degrading to catalog value", modelId);
            return null;
        }
    }
}
