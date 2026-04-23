namespace GuideAntsApi.Services.Components;

/// <summary>
/// Provides locking mechanism to prevent concurrent sync operations on the same notebook.
/// </summary>
public interface INotebookLockService
{
    /// <summary>
    /// Attempts to acquire a lock for the specified notebook.
    /// </summary>
    /// <param name="notebookId">The notebook identifier.</param>
    /// <param name="timeout">Maximum time to wait for the lock.</param>
    /// <returns>A lock handle that must be disposed to release the lock. Check Acquired property to see if lock was obtained.</returns>
    Task<NotebookLockHandle> TryAcquireAsync(Guid notebookId, TimeSpan timeout);
}

/// <summary>
/// Represents a lock handle that must be disposed to release the lock.
/// </summary>
public class NotebookLockHandle : IAsyncDisposable
{
    private readonly SemaphoreSlim? _semaphore;
    private bool _disposed;

    /// <summary>
    /// Indicates whether the lock was successfully acquired.
    /// </summary>
    public bool Acquired { get; }

    internal NotebookLockHandle(bool acquired, SemaphoreSlim? semaphore = null)
    {
        Acquired = acquired;
        _semaphore = semaphore;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (Acquired && _semaphore != null)
        {
            _semaphore.Release();
        }

        await ValueTask.CompletedTask;
    }
}

