using FluentAssertions;
using GuideAntsApi.BackgroundJobs;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class ConversationLockJobGateTests
{
    private static ConversationLockGateOptions CreateOptions() => new();

    [TestMethod]
    public void ShouldDeferJobType_ReturnsTrueForGatedTypeWhenLockActiveAndBothLocalAi()
    {
        var options = CreateOptions();

        ConversationLockJobGate.ShouldDeferJobType("IndexDirectTextFile", options, hasActiveConversationLock: true, bothChatAndEmbeddingsUseLocalAi: true)
            .Should().BeTrue();
        ConversationLockJobGate.ShouldDeferJobType("ExtractNotebookFileMarkdown", options, hasActiveConversationLock: true, bothChatAndEmbeddingsUseLocalAi: true)
            .Should().BeTrue();
    }

    [TestMethod]
    public void ShouldDeferJobType_ReturnsFalseForGatedTypeWhenLockActiveButNotBothLocalAi()
    {
        var options = CreateOptions();

        ConversationLockJobGate.ShouldDeferJobType("IndexDirectTextFile", options, hasActiveConversationLock: true, bothChatAndEmbeddingsUseLocalAi: false)
            .Should().BeFalse();
    }

    [TestMethod]
    public void ShouldDeferJobType_ReturnsFalseForNonGatedTypeWhenLockActive()
    {
        var options = CreateOptions();

        ConversationLockJobGate.ShouldDeferJobType("Test", options, hasActiveConversationLock: true, bothChatAndEmbeddingsUseLocalAi: true)
            .Should().BeFalse();
        ConversationLockJobGate.ShouldDeferJobType("SyncNotebook", options, hasActiveConversationLock: true, bothChatAndEmbeddingsUseLocalAi: true)
            .Should().BeTrue();
        ConversationLockJobGate.ShouldDeferJobType("RetentionCleanup", options, hasActiveConversationLock: true, bothChatAndEmbeddingsUseLocalAi: true)
            .Should().BeFalse();
    }

    [TestMethod]
    public void ShouldDeferJobType_ReturnsFalseWhenLockInactive()
    {
        var options = CreateOptions();

        ConversationLockJobGate.ShouldDeferJobType("IndexDirectTextFile", options, hasActiveConversationLock: false, bothChatAndEmbeddingsUseLocalAi: true)
            .Should().BeFalse();
    }

    [TestMethod]
    public void ShouldDeferJobType_ReturnsFalseWhenGateDisabled()
    {
        var options = CreateOptions();
        options.Enabled = false;

        ConversationLockJobGate.ShouldDeferJobType("IndexDirectTextFile", options, hasActiveConversationLock: true, bothChatAndEmbeddingsUseLocalAi: true)
            .Should().BeFalse();
    }
}
