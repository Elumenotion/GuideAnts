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
    private readonly ILlamaStackRuntimeClientProvider _runtimeClients;
    private readonly ILogger<ConversationContextStatusService> _logger;

    public ConversationContextStatusService(
        IServiceScopeFactory scopeFactory,
        IContextWindowResolver resolver,
        ILlamaStackRuntimeClientProvider runtimeClients,
        ILogger<ConversationContextStatusService> logger)
    {
        _scopeFactory = scopeFactory;
        _resolver = resolver;
        _runtimeClients = runtimeClients;
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

        // Token count: the provider-reported prompt size of the final round of the newest
        // completed turn. This is the real number of tokens the model saw, not an estimate.
        // No estimation from character counts is performed here.
        int? estimatedPromptTokens = null;
        var estimateSource = ContextEstimateSource.None;
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
                    estimatedPromptTokens = usage.LastRoundPromptTokens.Value;
                    estimateSource = ContextEstimateSource.ProviderUsage;
                    break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping unparseable UsageJson for conversation {ConversationId}", conversationId);
            }
        }

        int? window = null;
        var windowSource = ContextWindowSource.Unknown;
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var isLlama = await IsLlamaModelAsync(db, modelId, ct);
            if (isLlama)
            {
                // Local llama models: the loaded model's server is the single source of truth for
                // the window. meta.n_ctx from that server is the runtime value (after memory-based
                // auto-derivation). When the model is not loaded (or the query fails) the window
                // stays null -- no catalog/learned fallback is applied to llama rows.
                var live = await TryGetLiveContextSizeAsync(db, modelId, ct);
                window = live;
                windowSource = live is > 0 ? ContextWindowSource.LiveRuntime : ContextWindowSource.Unknown;
            }
            else
            {
                var info = _resolver.Resolve(modelId, null);
                window = info.ContextWindowTokens;
                windowSource = info.Source;
            }
        }

        return new ConversationContextStatusDto(window, estimatedPromptTokens, boundaryTurnIndex, estimateSource, modelId, windowSource);
    }

    private static async Task<bool> IsLlamaModelAsync(ApplicationDbContext db, string modelId, CancellationToken ct)
    {
        var provider = await db.Models
            .AsNoTracking()
            .Where(m => m.ModelId == modelId)
            .Select(m => m.Provider)
            .FirstOrDefaultAsync(ct);
        return string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase);
    }

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

            var config = LocalRuntimeConfigurationParser.Parse(modelId, row.RuntimeConfigJson);
            var client = _runtimeClients.GetClientForStack(config.StackBaseUrl, config.StackApiKey)
                ?? _runtimeClients.Global;

            // The model's own server is the single source of truth for the effective context
            // size. meta.n_ctx is the runtime value (after memory-based auto-derivation), not
            // the ini's configured value, and is non-null only while the model is loaded.
            var response = await client.ListModelsAsync(ct);
            return response.Data
                .FirstOrDefault(m =>
                    string.Equals(m.Id, config.RouterModelId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(m.Status?.Value, "loaded", StringComparison.OrdinalIgnoreCase))
                ?.Meta?.NCtx;
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
