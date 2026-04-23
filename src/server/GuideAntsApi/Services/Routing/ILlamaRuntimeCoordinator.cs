using System.Collections.Concurrent;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Serializes load / unload operations targeting a single llama runtime alias
/// (R-12.10). All callers that want to mutate a router alias (NotebookModelRuntimeService
/// load, settings-UI load/unload) must acquire the alias lock first so that two
/// concurrent operations on the same alias cannot interleave.
/// </summary>
public interface ILlamaRuntimeCoordinator
{
    /// <summary>
    /// Waits until the alias-scoped lock is free and then returns a disposable
    /// handle that releases the lock. Disposing is idempotent.
    /// </summary>
    Task<IAsyncDisposable> AcquireAliasLockAsync(string alias, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase F (R-6.10): attempts to acquire the alias-scoped lock without
    /// waiting. Returns <c>null</c> if another caller currently holds it so
    /// that HTTP endpoints can surface a deterministic 409 rather than stalling.
    /// Does NOT add a new lock — this reuses the same per-alias semaphore that
    /// <see cref="AcquireAliasLockAsync"/> uses.
    /// </summary>
    IAsyncDisposable? TryAcquireAliasLock(string alias);

    /// <summary>
    /// Phase F (R-6.10): diagnostic-only snapshot of whether the alias lock is
    /// currently held. Never allocates the lock — a missing semaphore reports
    /// <c>false</c>. Used by the runtime status endpoint to surface in-flight
    /// load/unload without mutating any state.
    /// </summary>
    bool IsAliasLocked(string alias);
}

public sealed class LlamaRuntimeCoordinator : ILlamaRuntimeCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _aliasLocks =
        new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAliasLockAsync(string alias, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Alias must be provided.", nameof(alias));
        }

        var semaphore = _aliasLocks.GetOrAdd(alias, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    public IAsyncDisposable? TryAcquireAliasLock(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Alias must be provided.", nameof(alias));
        }

        var semaphore = _aliasLocks.GetOrAdd(alias, _ => new SemaphoreSlim(1, 1));
        if (!semaphore.Wait(0))
        {
            return null;
        }

        return new Releaser(semaphore);
    }

    public bool IsAliasLocked(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        return _aliasLocks.TryGetValue(alias, out var semaphore)
            && semaphore.CurrentCount == 0;
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _released;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
