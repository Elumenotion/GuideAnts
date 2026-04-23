using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAnts.Usage;

/// <summary>
/// Parameters for creating an agent invocation record.
/// </summary>
public sealed record AgentInvocationParams(
    Guid ParentConversationId,
    int ParentTurnIndex,
    string? TriggeringToolCallId,
    Guid? ParentInvocationId,
    Guid AssistantId,
    string AssistantName,
    string? ModelDeploymentId,
    string Instructions,
    string? ContextMessageJson,
    string? Evaluator,
    int Depth);

/// <summary>
/// Parameters for adding a message to an agent invocation.
/// </summary>
public sealed record AgentInvocationMessageParams(
    Guid InvocationId,
    int Sequence,
    ChatRole Role,
    string Content,
    string? ToolCallsJson = null,
    string? FunctionName = null,
    string? ToolCallId = null);

/// <summary>
/// Parameters for completing an agent invocation.
/// </summary>
public sealed record AgentInvocationCompletionParams(
    Guid InvocationId,
    string Status,
    string? ErrorMessage,
    string? UsageJson,
    int LlmRoundTrips,
    int ToolCallCount,
    long DurationMs);

/// <summary>
/// Async interface for recording agent invocation threads.
/// </summary>
public interface IAgentInvocationRecorder
{
    /// <summary>
    /// Creates a new agent invocation record.
    /// </summary>
    Task<Guid> CreateInvocationAsync(AgentInvocationParams parameters, CancellationToken ct = default);

    /// <summary>
    /// Adds a message to an existing invocation.
    /// </summary>
    Task AddMessageAsync(AgentInvocationMessageParams parameters, CancellationToken ct = default);

    /// <summary>
    /// Marks an invocation as completed with final metrics.
    /// </summary>
    Task CompleteInvocationAsync(AgentInvocationCompletionParams parameters, CancellationToken ct = default);
}

/// <summary>
/// Synchronous interface for recording agent invocation threads.
/// Used in contexts where async is not convenient (static tool methods).
/// </summary>
public interface IAgentInvocationRecorderSync
{
    /// <summary>
    /// Creates a new agent invocation record.
    /// </summary>
    Guid CreateInvocation(AgentInvocationParams parameters);

    /// <summary>
    /// Adds a message to an existing invocation.
    /// </summary>
    void AddMessage(AgentInvocationMessageParams parameters);

    /// <summary>
    /// Marks an invocation as completed with final metrics.
    /// </summary>
    void CompleteInvocation(AgentInvocationCompletionParams parameters);
}

/// <summary>
/// Entity Framework implementation of <see cref="IAgentInvocationRecorder"/>.
/// </summary>
public sealed class EfAgentInvocationRecorder : IAgentInvocationRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfAgentInvocationRecorder> _log;

    /// <summary>
    /// Initializes a new instance of <see cref="EfAgentInvocationRecorder"/>.
    /// </summary>
    /// <param name="scopeFactory">Factory for creating database scopes.</param>
    /// <param name="log">Logger instance.</param>
    public EfAgentInvocationRecorder(IServiceScopeFactory scopeFactory, ILogger<EfAgentInvocationRecorder> log)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc/>
    public async Task<Guid> CreateInvocationAsync(AgentInvocationParams p, CancellationToken ct = default)
    {
        var invocation = new AgentInvocation
        {
            ParentConversationId = p.ParentConversationId,
            ParentTurnIndex = p.ParentTurnIndex,
            TriggeringToolCallId = p.TriggeringToolCallId,
            ParentInvocationId = p.ParentInvocationId,
            AssistantId = p.AssistantId,
            AssistantName = p.AssistantName,
            ModelDeploymentId = p.ModelDeploymentId,
            Instructions = p.Instructions,
            ContextMessageJson = p.ContextMessageJson,
            Evaluator = p.Evaluator,
            Depth = p.Depth,
            Status = "running"
        };

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.AgentInvocations.Add(invocation);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.LogDebug("Created AgentInvocation {InvocationId} for assistant {AssistantName} at depth {Depth}",
            invocation.Id, p.AssistantName, p.Depth);

        return invocation.Id;
    }

    /// <inheritdoc/>
    public async Task AddMessageAsync(AgentInvocationMessageParams p, CancellationToken ct = default)
    {
        var message = new AgentInvocationMessage
        {
            AgentInvocationId = p.InvocationId,
            Sequence = p.Sequence,
            Role = p.Role,
            Content = p.Content,
            ToolCallsJson = p.ToolCallsJson,
            FunctionName = p.FunctionName,
            ToolCallId = p.ToolCallId
        };

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.AgentInvocationMessages.Add(message);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.LogDebug("Added message {Sequence} ({Role}) to AgentInvocation {InvocationId}",
            p.Sequence, p.Role, p.InvocationId);
    }

    /// <inheritdoc/>
    public async Task CompleteInvocationAsync(AgentInvocationCompletionParams p, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var invocation = await db.AgentInvocations.FindAsync([p.InvocationId], ct).ConfigureAwait(false);
        if (invocation == null)
        {
            _log.LogWarning("AgentInvocation {InvocationId} not found for completion", p.InvocationId);
            return;
        }

        invocation.Status = p.Status;
        invocation.ErrorMessage = p.ErrorMessage;
        invocation.UsageJson = p.UsageJson;
        invocation.LlmRoundTrips = p.LlmRoundTrips;
        invocation.ToolCallCount = p.ToolCallCount;
        invocation.Completed = DateTime.UtcNow;
        invocation.DurationMs = p.DurationMs;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.LogDebug("Completed AgentInvocation {InvocationId} with status {Status} in {DurationMs}ms",
            p.InvocationId, p.Status, p.DurationMs);
    }
}

/// <summary>
/// Blocking wrapper around <see cref="IAgentInvocationRecorder"/>.
/// </summary>
public sealed class BlockingAgentInvocationRecorder : IAgentInvocationRecorderSync
{
    private readonly IAgentInvocationRecorder _inner;

    /// <summary>
    /// Initializes a new instance of <see cref="BlockingAgentInvocationRecorder"/>.
    /// </summary>
    /// <param name="inner">The async recorder to wrap.</param>
    public BlockingAgentInvocationRecorder(IAgentInvocationRecorder inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc/>
    public Guid CreateInvocation(AgentInvocationParams parameters)
        => _inner.CreateInvocationAsync(parameters).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public void AddMessage(AgentInvocationMessageParams parameters)
        => _inner.AddMessageAsync(parameters).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public void CompleteInvocation(AgentInvocationCompletionParams parameters)
        => _inner.CompleteInvocationAsync(parameters).GetAwaiter().GetResult();
}

/// <summary>
/// Factory for non-DI creation of agent invocation recorder.
/// </summary>
public static class AgentInvocationRecorderFactory
{
    /// <summary>
    /// Creates a blocking <see cref="IAgentInvocationRecorderSync"/> using the connection string
    /// from environment variable <c>ConnectionStrings:DefaultConnection</c>.
    /// </summary>
    public static IAgentInvocationRecorderSync CreateFromEnv(ILoggerFactory? loggerFactory = null)
    {
        var conn = Environment.GetEnvironmentVariable("ConnectionStrings:DefaultConnection")
            ?? throw new InvalidOperationException("Env var 'ConnectionStrings:DefaultConnection' not set.");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(conn, sql =>
        {
            sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
            sql.CommandTimeout(60);
        }));
        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var logger = (loggerFactory ?? LoggerFactory.Create(b => { }))
            .CreateLogger<EfAgentInvocationRecorder>();

        var asyncRecorder = new EfAgentInvocationRecorder(scopeFactory, logger);
        return new BlockingAgentInvocationRecorder(asyncRecorder);
    }
}

