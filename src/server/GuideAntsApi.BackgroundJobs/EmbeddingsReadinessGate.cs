namespace GuideAntsApi.BackgroundJobs;

///<summary>
/// Contract for warming the local embeddings engine before a gated job is
/// claimed. Implemented by the API host (which owns
///<c>ILocalServiceLoadService</c>); resolved optionally by the
///<see cref="BackgroundJobProcessor"/>.
///</summary>
///<remarks>
/// The implementation must be safe for concurrent callers: when several gated
/// job types hit the gate in the same poll cycle while the engine is unloaded,
/// exactly one load may be in flight and the remaining callers must await the
/// same result rather than issuing duplicate load requests.
///</remarks>
public interface IEmbeddingsReadinessGate
{
    ///<summary>
    /// True when the resolved embeddings mode routes through the local provider.
    ///</summary>
    Task<bool> UsesLocalEmbeddingsAsync(CancellationToken cancellationToken = default);

    ///<summary>
    /// Probe the local embeddings engine and load the persisted model when it is
    /// not loaded. A load failure must not throw; the returned tuple carries
    ///<c>false</c> plus an error message so the caller defers the claim.
    ///</summary>
    Task<(bool Loaded, string? Error)> EnsureLocalEmbeddingsLoadedAsync(
        CancellationToken cancellationToken = default);
}

public static class EmbeddingsJobGate
{
    ///<summary>
    /// The claim is deferred only when the gate is enabled, the job type is gated,
    /// the embeddings mode routes through the local provider, and the engine could
    /// not be confirmed loaded.
    ///</summary>
    public static bool ShouldDeferJobType(
        string jobType,
        EmbeddingsJobGateOptions options,
        bool usesLocalEmbeddings,
        bool loaded)
    {
        return options.Enabled
               && usesLocalEmbeddings
               && !loaded
               && options.GatedJobTypes.Contains(jobType);
    }
}
