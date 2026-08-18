using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationStreamRunRegistryTests
{
    [TestMethod]
    public void IsActive_is_true_only_while_registered()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();

        registry.IsActive(turnId).Should().BeFalse();

        _ = registry.Register(turnId, CancellationToken.None);
        registry.IsActive(turnId).Should().BeTrue();

        registry.Unregister(turnId);
        registry.IsActive(turnId).Should().BeFalse();
    }

    [TestMethod]
    public void RequestCancel_cancels_active_run_token()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();
        var token = registry.Register(turnId, CancellationToken.None);

        token.IsCancellationRequested.Should().BeFalse();
        registry.RequestCancel(turnId).Should().BeTrue();
        token.IsCancellationRequested.Should().BeTrue();
    }
}
