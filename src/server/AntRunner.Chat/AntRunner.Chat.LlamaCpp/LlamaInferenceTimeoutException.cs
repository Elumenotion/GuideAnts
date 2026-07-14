using AntRunner.Chat.Abstractions;

namespace AntRunner.Chat.LlamaCpp;

/// <summary>
/// Indicates that GuideAnts' own llama.cpp inference deadline expired. This is distinct from
/// caller cancellation: the runtime may still be generating and must be recovered before new
/// work is admitted.
/// </summary>
public sealed class LlamaInferenceTimeoutException : TimeoutException, IChatRunFatalException
{
    public LlamaInferenceTimeoutException(
        string routerModelId,
        int timeoutSeconds,
        Exception? innerException = null)
        : base(
            $"The local model '{routerModelId}' exceeded its {timeoutSeconds}-second inference deadline. "
            + "The runtime is being forcefully recovered.",
            innerException)
    {
        RouterModelId = routerModelId;
        TimeoutSeconds = timeoutSeconds;
    }

    public string RouterModelId { get; }

    public int TimeoutSeconds { get; }
}

/// <summary>
/// Inversion point implemented by the host application. The llama client reports a proven
/// internal timeout without taking a dependency on runtime administration infrastructure.
/// </summary>
public interface ILlamaInferenceTimeoutObserver
{
    /// <summary>
    /// Rejects admission while recovery owns the router alias.
    /// </summary>
    Task EnsureInferenceAvailableAsync(
        string? routerModelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts or joins forceful recovery for the timed-out router alias.
    /// Implementations must not throw from the returned task.
    /// </summary>
    Task<LlamaInferenceRecoveryResult> RequestRecoveryAsync(
        string routerModelId,
        int timeoutSeconds);
}

public sealed record LlamaInferenceRecoveryResult(
    string RouterModelId,
    bool Succeeded,
    bool EscalatedToServerRestart,
    string? Error);

public sealed class NullLlamaInferenceTimeoutObserver : ILlamaInferenceTimeoutObserver
{
    public static NullLlamaInferenceTimeoutObserver Instance { get; } = new();

    public Task EnsureInferenceAvailableAsync(
        string? routerModelId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<LlamaInferenceRecoveryResult> RequestRecoveryAsync(
        string routerModelId,
        int timeoutSeconds) =>
        Task.FromResult(new LlamaInferenceRecoveryResult(
            routerModelId,
            Succeeded: false,
            EscalatedToServerRestart: false,
            Error: "No inference timeout recovery observer is configured."));
}
