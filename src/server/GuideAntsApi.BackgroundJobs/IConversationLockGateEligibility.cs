namespace GuideAntsApi.BackgroundJobs;

/// <summary>
/// Determines whether conversation-lock gating should apply for this deployment.
/// Gating is only meaningful when chat and embeddings both route through local AI.
/// </summary>
public interface IConversationLockGateEligibility
{
    Task<bool> BothUseLocalAiAsync(CancellationToken cancellationToken = default);
}
