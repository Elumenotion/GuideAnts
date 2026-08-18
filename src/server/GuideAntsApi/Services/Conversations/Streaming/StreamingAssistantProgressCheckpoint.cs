namespace GuideAntsApi.Services.Conversations.Streaming;

/// <summary>
/// Counts streamed deltas and signals when a persistence checkpoint should run.
/// Buffering lives in the stream engine; this type is O(1) per delta.
/// </summary>
internal sealed class StreamingAssistantProgressCheckpoint
{
    private readonly int _flushInterval;
    private int _flushCounter;

    public StreamingAssistantProgressCheckpoint(int flushInterval = 20)
    {
        if (flushInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flushInterval));
        }

        _flushInterval = flushInterval;
    }

    public int FlushCounter => _flushCounter;

    /// <summary>
    /// Records one delta. Returns true when a checkpoint should be persisted.
    /// </summary>
    public bool ShouldCheckpoint()
    {
        _flushCounter++;
        return _flushCounter % _flushInterval == 0;
    }
}
