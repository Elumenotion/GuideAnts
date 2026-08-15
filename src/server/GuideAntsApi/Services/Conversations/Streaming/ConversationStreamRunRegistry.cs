using System.Collections.Concurrent;

namespace GuideAntsApi.Services.Conversations.Streaming;

/// <summary>
/// Tracks in-process conversation stream runs so explicit Stop can cancel the background worker.
/// </summary>
public sealed class ConversationStreamRunRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeRuns = new();

    public CancellationToken Register(Guid turnId, CancellationToken externalToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _activeRuns[turnId] = linked;
        return linked.Token;
    }

    public void Unregister(Guid turnId)
    {
        if (_activeRuns.TryRemove(turnId, out var cts))
        {
            cts.Dispose();
        }
    }

    public bool RequestCancel(Guid turnId)
    {
        if (!_activeRuns.TryGetValue(turnId, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
