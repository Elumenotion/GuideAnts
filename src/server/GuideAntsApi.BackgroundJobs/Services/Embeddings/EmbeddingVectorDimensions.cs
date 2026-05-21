namespace GuideAntsApi.BackgroundJobs.Services.Embeddings;

internal static class EmbeddingVectorDimensions
{
    internal const int Target = 1536;

    internal static float[] NormalizeToTarget(float[] source)
    {
        if (source.Length == Target)
        {
            return source;
        }

        var normalized = new float[Target];
        var copyCount = Math.Min(source.Length, Target);
        Array.Copy(source, normalized, copyCount);
        return normalized;
    }

    internal static float[][] NormalizeBatchToTarget(float[][] vectors)
    {
        if (vectors.Length == 0)
        {
            return vectors;
        }

        var normalized = new float[vectors.Length][];
        for (var i = 0; i < vectors.Length; i++)
        {
            normalized[i] = NormalizeToTarget(vectors[i]);
        }

        return normalized;
    }
}
