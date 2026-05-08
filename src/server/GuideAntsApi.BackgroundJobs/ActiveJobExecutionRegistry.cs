using System.Collections.Concurrent;

namespace GuideAntsApi.BackgroundJobs;

public readonly record struct ActiveJobExecutionKey(Guid JobId, Guid ClaimToken);

public interface IActiveJobExecutionRegistry
{
    void MarkActive(Guid jobId, Guid claimToken);
    void Touch(Guid jobId, Guid claimToken);
    void MarkInactive(Guid jobId, Guid claimToken);
    HashSet<ActiveJobExecutionKey> GetActiveSince(DateTime utcCutoff);
}

internal sealed class ActiveJobExecutionRegistry : IActiveJobExecutionRegistry
{
    private readonly ConcurrentDictionary<ActiveJobExecutionKey, DateTime> _active = new();

    public void MarkActive(Guid jobId, Guid claimToken)
    {
        if (claimToken == Guid.Empty)
        {
            return;
        }

        _active[new ActiveJobExecutionKey(jobId, claimToken)] = DateTime.UtcNow;
    }

    public void Touch(Guid jobId, Guid claimToken)
    {
        if (claimToken == Guid.Empty)
        {
            return;
        }

        _active[new ActiveJobExecutionKey(jobId, claimToken)] = DateTime.UtcNow;
    }

    public void MarkInactive(Guid jobId, Guid claimToken)
    {
        if (claimToken == Guid.Empty)
        {
            return;
        }

        _active.TryRemove(new ActiveJobExecutionKey(jobId, claimToken), out _);
    }

    public HashSet<ActiveJobExecutionKey> GetActiveSince(DateTime utcCutoff)
    {
        var snapshot = new HashSet<ActiveJobExecutionKey>();
        foreach (var entry in _active)
        {
            if (entry.Value >= utcCutoff)
            {
                snapshot.Add(entry.Key);
            }
        }

        return snapshot;
    }
}
