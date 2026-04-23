using System.Collections.Concurrent;

namespace GuideAntsApi.Services.Components;

/// <summary>
/// In-memory implementation of notebook locking service using SemaphoreSlim.
/// </summary>
public class InMemoryNotebookLockService : INotebookLockService
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<NotebookLockHandle> TryAcquireAsync(Guid notebookId, TimeSpan timeout)
    {
        var semaphore = _locks.GetOrAdd(notebookId, _ => new SemaphoreSlim(1, 1));
        
        var acquired = await semaphore.WaitAsync(timeout);
        
        return new NotebookLockHandle(acquired, acquired ? semaphore : null);
    }
}

