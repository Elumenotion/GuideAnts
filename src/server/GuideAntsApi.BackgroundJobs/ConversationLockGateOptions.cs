namespace GuideAntsApi.BackgroundJobs;

/// <summary>
/// Defers extraction/indexing job claims while a conversation lock is active.
/// The gate only applies when <see cref="IConversationLockGateEligibility"/> reports
/// that both default chat and embeddings route through local AI.
/// </summary>
public class ConversationLockGateOptions
{
    public bool Enabled { get; set; } = true;

    public int LogThrottleSeconds { get; set; } = 60;

    public HashSet<string> GatedJobTypes { get; set; } = new(StringComparer.Ordinal)
    {
        "SyncNotebook",
        "ExtractContentVersionMarkdown",
        "ExtractNotebookFileMarkdown",
        "ExtractAssistantFileMarkdown",
        "TranscribeContentVersionMarkdown",
        "TranscribeNotebookFileMarkdown",
        "IndexContentMarkdownShadow",
        "IndexNotebookMarkdownShadow",
        "IndexAssistantFileMarkdownShadow",
        "IndexDirectTextFile",
    };
}
