using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GuideAntsApi.DataModel;

namespace GuideAnts.Usage;

/// <summary>
/// Entity-Framework implementation of <see cref="IUsageRecorder"/>.
/// </summary>
public sealed class EfUsageRecorder : IUsageRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfUsageRecorder> _log;

    /// <summary>
    /// Initializes a new instance of <see cref="EfUsageRecorder"/>.
    /// </summary>
    /// <param name="scopeFactory">The factory used to create <see cref="ApplicationDbContext"/> instances.</param>
    /// <param name="log">Logger instance.</param>
    public EfUsageRecorder(IServiceScopeFactory scopeFactory, ILogger<EfUsageRecorder> log)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc/>
    public async Task RecordAsync(
        Guid projectId,
        Guid notebookId,
        UsageCategory category,
        string service,
        string operation,
        UsageMetrics metrics,
        decimal costUsd = 0m,
        Guid? conversationId = null,
        Guid? contentFileId = null,
        Guid? notebookFileId = null,
        string? modelDeploymentId = null,
        string? metadataJson = null,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        Guid? notebookConversationMessageId = null,
        CancellationToken ct = default,
        Guid? publishedGuideId = null,
        string? sourceChannel = null,
        string? externalRequestId = null,
        string? externalUserIdentity = null)
    {
        // Validation – throw early, never silently swallow mistakes.
        // NotebookId is optional - project-level operations can use Guid.Empty
        if (string.IsNullOrWhiteSpace(service)) throw new ArgumentException("Service is required", nameof(service));
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("Operation is required", nameof(operation));
        
        // Business rule validation: ONLY applies when there's a ConversationId (i.e., conversational context)
        // Background processes without conversations don't need these attribution fields
        if (conversationId.HasValue)
        {
            // Business rule: When in a conversation, AssistantId is required
            if (!assistantId.HasValue)
            {
                _log.LogWarning(
                    "UsageEvent recorded in conversation context without AssistantId. Service={Service}, Operation={Operation}, " +
                    "Category={Category}, ConversationId={ConversationId}. " +
                    "Business rule: Conversational usage must have an AssistantId.",
                    service, operation, category, conversationId);
            }
            
            // Business rule: When in a conversation, exactly one of AgentInvocationId OR NotebookConversationMessageId should be provided
            var hasAgentInvocation = agentInvocationId.HasValue;
            var hasConversationMessage = notebookConversationMessageId.HasValue;
            
            if (!hasAgentInvocation && !hasConversationMessage)
            {
                _log.LogWarning(
                    "UsageEvent recorded in conversation context without AgentInvocationId or NotebookConversationMessageId. " +
                    "Service={Service}, Operation={Operation}, Category={Category}, ConversationId={ConversationId}, AssistantId={AssistantId}. " +
                    "Business rule: Conversational usage must have exactly one of AgentInvocationId or NotebookConversationMessageId.",
                    service, operation, category, conversationId, assistantId);
            }
            else if (hasAgentInvocation && hasConversationMessage)
            {
                _log.LogWarning(
                    "UsageEvent recorded with BOTH AgentInvocationId and NotebookConversationMessageId. " +
                    "Service={Service}, Operation={Operation}, Category={Category}, ConversationId={ConversationId}, " +
                    "AssistantId={AssistantId}, AgentInvocationId={AgentInvocationId}, NotebookConversationMessageId={NotebookConversationMessageId}. " +
                    "Business rule: Conversational usage should have only one of AgentInvocationId or NotebookConversationMessageId.",
                    service, operation, category, conversationId, assistantId, agentInvocationId, notebookConversationMessageId);
            }
        }

        // Calculate provider base cost if not already provided
        var baseCostUsd = costUsd;
        if (baseCostUsd == 0m)
        {
            baseCostUsd = category switch
            {
                UsageCategory.ChatCompletion => ChatCostCalculator.CalculateCost(metrics, modelDeploymentId),
                UsageCategory.ImageGeneration => 0.04m, // $0.04 per image generation call
                UsageCategory.SpeechSynthesis => metrics.ValueOther * 0.00003m, // $30 per 1M characters (Neural HD)
                UsageCategory.SpeechTranscription => (metrics.ValueOther / 3600.0m) * 0.36m, // $0.36 per audio hour
                UsageCategory.DocumentExtraction => metrics.ValueOther * 0.0015m, // $1.50 per 1K pages = $0.0015 per page (prebuilt-read)
                UsageCategory.StorageUploaded => 0.001m, // $0.001 per file operation
                UsageCategory.StorageSystemGenerated => 0.001m, // $0.001 per file operation
                // Tool calls are typically internal operations whose true cost is captured elsewhere (e.g., AgentInvocations).
                // We now treat them as zero-cost events **unless** the operation is a remote crawl which incurs an external fee.
                UsageCategory.ToolCall =>
                    // Crew-bridge calls include an assistantName field in their metadata (function arguments).
                    // These are internal invokes whose true cost is captured by the downstream AgentInvocation, so charge zero here.
                    (metadataJson?.Contains("\"assistantName\"") == true)
                        ? 0m
                        : operation.Equals("crawl", StringComparison.OrdinalIgnoreCase)
                            ? 0.005m                      // $0.005 per external web crawl
                            : (operation.Equals("ReadWeb", StringComparison.OrdinalIgnoreCase) ||
                               operation.Equals("GetContentFromUrl", StringComparison.OrdinalIgnoreCase))
                                ? 0m                      // Read Web bridge + content fetch are zero-cost to the user
                                : 0.001m,                 // $0.001 default for other tool calls
                UsageCategory.Search => 0.001m, // $0.001 per search operation
                _ => 0m
            };
        }

        // Apply markup percentage for customer-facing charge (20% default)
        const decimal defaultMarkupPercent = 1.20m;
        decimal? finalChargeUsd = null;
        if (baseCostUsd > 0m)
        {
            var withMarkup = baseCostUsd * defaultMarkupPercent;
            // Store at 4-decimal precision; rounding to cents happens at billing/invoicing time.
            finalChargeUsd = Math.Round(withMarkup, 4, MidpointRounding.AwayFromZero);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Guid? resolvedInvokingAssistantId = null;
        if (agentInvocationId.HasValue && !resolvedInvokingAssistantId.HasValue)
        {
            Guid? currentInvocationId = agentInvocationId.Value;
            string? rootTriggeringToolCallId = null;

            while (currentInvocationId.HasValue)
            {
                var invocation = await db.AgentInvocations
                    .Where(ai => ai.Id == currentInvocationId.Value)
                    .Select(ai => new { ai.ParentInvocationId, ai.TriggeringToolCallId })
                    .FirstOrDefaultAsync(ct);

                if (invocation == null)
                {
                    break;
                }

                rootTriggeringToolCallId = invocation.TriggeringToolCallId;

                if (invocation.ParentInvocationId == null)
                {
                    break;
                }

                currentInvocationId = invocation.ParentInvocationId;
            }

            if (!string.IsNullOrEmpty(rootTriggeringToolCallId))
            {
                resolvedInvokingAssistantId = await db.NotebookConversationMessages
                    .Where(m => m.ToolCallId == rootTriggeringToolCallId)
                    .Select(m => m.AssistantId)
                    .FirstOrDefaultAsync(ct);
            }
        }

        var evt = new GuideAntsApi.DataModel.Models.UsageEvent
        {
            ProjectId = projectId,
            NotebookId = notebookId,
            ConversationId = conversationId,
            ContentFileId = contentFileId,
            NotebookFileId = notebookFileId,
            PublishedGuideId = publishedGuideId,
            SourceChannel = sourceChannel,
            ExternalRequestId = externalRequestId,
            ExternalUserIdentity = externalUserIdentity,
            AssistantId = assistantId,
            InvokingAssistantId = agentInvocationId.HasValue ? resolvedInvokingAssistantId : null,
            AgentInvocationId = agentInvocationId,
            NotebookConversationMessageId = notebookConversationMessageId,
            Category = (GuideAntsApi.DataModel.Models.UsageCategory)(int)category, // enum parity
            Service = service,
            Operation = operation,
            ModelDeploymentId = modelDeploymentId,
            ValueInput = metrics.ValueInput,
            ValueCachedInput = metrics.ValueCachedInput,
            ValueReasoning = metrics.ValueReasoning,
            ValueOutput = metrics.ValueOutput,
            ValueOther = metrics.ValueOther,
            MetadataJson = metadataJson,
            CostUsd = baseCostUsd == 0m ? (decimal?)null : baseCostUsd,
            MarkupPercent = defaultMarkupPercent,
            ChargeUsd = finalChargeUsd
        };

        db.UsageEvents.Add(evt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _log.LogDebug("Usage event {UsageEventId} recorded (Category={Category})", evt.Id, evt.Category);

        // OSS-lite: usage is recorded for analytics/reporting only.
        // Owner wallet debit logic is intentionally removed with billing.
    }
}

/// <summary>
/// Simple synchronous wrapper around an async recorder – blocks the calling thread.
/// </summary>
public sealed class BlockingUsageRecorder : IUsageRecorderSync
{
    private readonly IUsageRecorder _inner;

    /// <summary>
    /// Initializes a new instance of <see cref="BlockingUsageRecorder"/>.
    /// </summary>
    /// <param name="inner">The asynchronous <see cref="IUsageRecorder"/> to wrap.</param>
    public BlockingUsageRecorder(IUsageRecorder inner) => _inner = inner;

    /// <inheritdoc/>
    public void RecordBlocking(
        Guid projectId,
        Guid notebookId,
        UsageCategory category,
        string service,
        string operation,
        UsageMetrics metrics,
        decimal costUsd = 0m,
        Guid? conversationId = null,
        Guid? contentFileId = null,
        Guid? notebookFileId = null,
        string? modelDeploymentId = null,
        string? metadataJson = null,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        Guid? notebookConversationMessageId = null,
        Guid? publishedGuideId = null,
        string? sourceChannel = null,
        string? externalRequestId = null,
        string? externalUserIdentity = null)
    {
        _inner.RecordAsync(
            projectId,
            notebookId,
            category,
            service,
            operation,
            metrics,
            costUsd,
            conversationId,
            contentFileId,
            notebookFileId,
            modelDeploymentId,
            metadataJson,
            assistantId,
            agentInvocationId,
            notebookConversationMessageId,
            CancellationToken.None,
            publishedGuideId,
            sourceChannel,
            externalRequestId,
            externalUserIdentity)
              .GetAwaiter()
              .GetResult();
    }
}

/// <summary>
/// Static factory for non-DI creation (e.g., console runners).
/// </summary>
public static class UsageRecorderFactory
{
    /// <summary>
    /// Creates a blocking <see cref="IUsageRecorderSync"/> using the default SQL connection string from
    /// the environment variable <c>ConnectionStrings:DefaultConnection</c>.
    /// </summary>
    /// <param name="loggerFactory">Factory used to create a logger for the recorder.</param>
    /// <returns>Blocking recorder instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the required environment variable is missing.</exception>
    public static IUsageRecorderSync CreateFromEnv(ILoggerFactory loggerFactory)
    {
        var conn = Environment.GetEnvironmentVariable("ConnectionStrings:DefaultConnection")
                   ?? throw new InvalidOperationException("Env var 'ConnectionStrings:DefaultConnection' not set.");

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(conn, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
                sql.CommandTimeout(60);
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(conn, sql =>
        {
            sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
            sql.CommandTimeout(60);
        }));
        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var logger = loggerFactory.CreateLogger<EfUsageRecorder>();

        var asyncRecorder = new EfUsageRecorder(scopeFactory, logger);
        return new BlockingUsageRecorder(asyncRecorder);
    }
}
