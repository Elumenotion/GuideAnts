namespace GuideAntsApi.Services.Conversations.Streaming;

/// <summary>
/// Signals when a streaming persistence checkpoint should run. Coalesces by byte volume,
/// elapsed time, and a per-second rate cap so hot streams do not overwhelm the database.
/// </summary>
internal sealed class StreamingAssistantProgressCheckpoint
{
    private const int ByteThreshold = 512;
    private const int MaxCheckpointsPerSecond = 4;
    private static readonly TimeSpan TimeThreshold = TimeSpan.FromMilliseconds(250);

    private int _bytesSinceLastCheckpoint;
    private DateTime _lastCheckpointUtc = DateTime.MinValue;
    private readonly Queue<DateTime> _recentCheckpoints = new();
    private int _flushCounter;

    public int FlushCounter => _flushCounter;

    /// <summary>
    /// Records one delta. Returns true when a checkpoint should be persisted.
    /// </summary>
    public bool ShouldCheckpoint(int deltaByteCount)
    {
        _flushCounter++;
        _bytesSinceLastCheckpoint += Math.Max(deltaByteCount, 0);

        var now = DateTime.UtcNow;
        if (_lastCheckpointUtc == DateTime.MinValue)
        {
            return TryScheduleCheckpoint(now);
        }

        if (_bytesSinceLastCheckpoint >= ByteThreshold)
        {
            return TryScheduleCheckpoint(now);
        }

        if (now - _lastCheckpointUtc >= TimeThreshold)
        {
            return TryScheduleCheckpoint(now);
        }

        return false;
    }

    private bool TryScheduleCheckpoint(DateTime now)
    {
        while (_recentCheckpoints.Count > 0
               && now - _recentCheckpoints.Peek() > TimeSpan.FromSeconds(1))
        {
            _recentCheckpoints.Dequeue();
        }

        if (_recentCheckpoints.Count >= MaxCheckpointsPerSecond)
        {
            return false;
        }

        _recentCheckpoints.Enqueue(now);
        _lastCheckpointUtc = now;
        _bytesSinceLastCheckpoint = 0;
        return true;
    }
}
