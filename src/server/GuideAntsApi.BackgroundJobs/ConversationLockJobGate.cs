namespace GuideAntsApi.BackgroundJobs;

public static class ConversationLockJobGate
{
    public static bool ShouldDeferJobType(
        string jobType,
        ConversationLockGateOptions options,
        bool hasActiveConversationLock,
        bool bothChatAndEmbeddingsUseLocalAi)
    {
        return options.Enabled
               && bothChatAndEmbeddingsUseLocalAi
               && hasActiveConversationLock
               && options.GatedJobTypes.Contains(jobType);
    }
}
