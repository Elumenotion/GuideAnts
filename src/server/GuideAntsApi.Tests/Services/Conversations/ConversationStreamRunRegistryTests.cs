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

        using var cts = registry.Register(turnId);
        registry.IsActive(turnId).Should().BeTrue();

        registry.Unregister(turnId);
        registry.IsActive(turnId).Should().BeFalse();
    }

    [TestMethod]
    public void RequestCancel_cancels_active_run_token()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();
        using var cts = registry.Register(turnId);

        cts.Token.IsCancellationRequested.Should().BeFalse();
        registry.RequestCancel(turnId).Should().BeTrue();
        cts.Token.IsCancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public void Register_is_not_linked_to_http_abort_token()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();
        using var httpCts = new CancellationTokenSource();
        using var workerCts = registry.Register(turnId);

        httpCts.Cancel();
        workerCts.Token.IsCancellationRequested.Should().BeFalse();
    }
}
