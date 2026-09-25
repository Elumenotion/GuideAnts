using System.Collections.Concurrent;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Remembers context-window sizes reported by provider rejections, so a model with no
/// catalog value still yields a usable estimate after one overflow. Metadata only —
/// recording a value never triggers compaction or alters request shaping.
/// </summary>
public interface ILearnedContextWindowCache
{
    void Record(string modelId, int? contextSize);
    int? Get(string modelId);
}

/// <inheritdoc />
public sealed class LearnedContextWindowCache : ILearnedContextWindowCache
{
    private readonly ConcurrentDictionary<string, int> _learned =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(string modelId, int? contextSize)
    {
        if (string.IsNullOrWhiteSpace(modelId) || contextSize is not > 0)
        {
            return;
        }

        // Keep the smallest observed window. The same model id can front deployments with
        // different limits (notably llama.cpp), and the smallest is the one that rejects.
        _learned.AddOrUpdate(
            modelId,
            contextSize.Value,
            (_, existing) => Math.Min(existing, contextSize.Value));
    }

    public int? Get(string modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && _learned.TryGetValue(modelId, out var value)
            ? value
            : null;
}
