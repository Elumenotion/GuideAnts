namespace GuideAntsApi.BackgroundJobs;

///<summary>
/// Gates job claims that consume the embeddings service on the local embeddings
/// engine being loaded. The gate only applies when the resolved embeddings mode
/// routes through the local provider (mirrors <see cref="ConversationLockGateOptions"/>).
///</summary>
public class EmbeddingsJobGateOptions
{
    public bool Enabled { get; set; } = true;

    public int LogThrottleSeconds { get; set; } = 60;

    ///<summary>
    /// Job types that consume embeddings via the hybrid indexer.
    ///</summary>
    public HashSet<string> GatedJobTypes { get; set; } = new(StringComparer.Ordinal)
    {
        "IndexContentMarkdownShadow",
        "IndexNotebookMarkdownShadow",
        "IndexAssistantFileMarkdownShadow",
        "IndexDirectTextFile",
        "RebuildEmbeddings",
    };
}
