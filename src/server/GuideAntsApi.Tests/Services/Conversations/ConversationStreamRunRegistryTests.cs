using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationStreamRunRegistryTests
{
    [TestMethod]
    public void IsInFlight_is_true_for_active_and_detached_runs()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();

        registry.IsInFlight(turnId).Should().BeFalse();

        using var cts = registry.Register(turnId);
        registry.IsInFlight(turnId).Should().BeTrue();
        registry.IsActive(turnId).Should().BeTrue();

        registry.Detach(turnId).Should().BeTrue();
        registry.IsInFlight(turnId).Should().BeTrue();
        registry.IsActive(turnId).Should().BeFalse();

        registry.Unregister(turnId);
        registry.IsInFlight(turnId).Should().BeFalse();
    }

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
        registry.IsHardStopRequested(turnId).Should().BeFalse();
    }

    [TestMethod]
    public void RequestHardStop_marks_active_run_before_cancelling_token()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();
        using var cts = registry.Register(turnId);

        registry.RequestHardStop(turnId).Should().BeTrue();

        registry.IsHardStopRequested(turnId).Should().BeTrue();
        cts.Token.IsCancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public void HardStop_marker_survives_detach_until_worker_unregisters()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();
        using var cts = registry.Register(turnId);

        registry.RequestHardStop(turnId).Should().BeTrue();
        registry.Detach(turnId).Should().BeTrue();

        registry.IsHardStopRequested(turnId).Should().BeTrue();

        registry.Unregister(turnId);
        registry.IsHardStopRequested(turnId).Should().BeFalse();
    }

    [TestMethod]
    public void Detach_removes_logical_ownership_without_waiting_for_worker_exit()
    {
        var registry = new ConversationStreamRunRegistry();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        using var cts = registry.Register(turnId, conversationId);

        registry.RequestCancel(turnId).Should().BeTrue();
        registry.Detach(turnId).Should().BeTrue();

        registry.IsActive(turnId).Should().BeFalse();
        registry.IsAnyActiveForConversation(conversationId).Should().BeFalse();
        cts.Token.IsCancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public void IsAnyActiveForConversation_excludes_completed_and_other_conversations()
    {
        var registry = new ConversationStreamRunRegistry();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var otherTurnId = Guid.NewGuid();
        using var cts = registry.Register(turnId, conversationId);
        using var otherCts = registry.Register(otherTurnId, Guid.NewGuid());

        registry.IsAnyActiveForConversation(conversationId).Should().BeTrue();
        registry.IsAnyActiveForConversation(conversationId, turnId).Should().BeFalse();

        registry.Unregister(turnId);
        registry.Unregister(otherTurnId);
        registry.IsAnyActiveForConversation(conversationId).Should().BeFalse();
    }

    [TestMethod]
    public async Task RequestCancelAsync_waits_until_worker_unregisters()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();
        using var cts = registry.Register(turnId);

        var cancellationTask = registry.RequestCancelAsync(
            turnId,
            TimeSpan.FromSeconds(1));

        cts.Token.IsCancellationRequested.Should().BeTrue();
        cancellationTask.IsCompleted.Should().BeFalse(
            "Stop must not report success while the worker still owns the turn");
        registry.IsActive(turnId).Should().BeTrue();

        registry.Unregister(turnId);

        (await cancellationTask).Should().Be(StreamCancellationResult.Completed);
        registry.IsActive(turnId).Should().BeFalse();
    }

    [TestMethod]
    public async Task RequestCancelAsync_is_idempotent_for_concurrent_stop_requests()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();
        using var cts = registry.Register(turnId);

        var first = registry.RequestCancelAsync(turnId, TimeSpan.FromSeconds(1));
        var second = registry.RequestCancelAsync(turnId, TimeSpan.FromSeconds(1));

        cts.Token.IsCancellationRequested.Should().BeTrue();
        registry.Unregister(turnId);

        (await Task.WhenAll(first, second))
            .Should()
            .OnlyContain(result => result == StreamCancellationResult.Completed);
    }

    [TestMethod]
    public async Task RequestCancelAsync_times_out_without_claiming_worker_stopped()
    {
        var registry = new ConversationStreamRunRegistry();
        var turnId = Guid.NewGuid();
        using var cts = registry.Register(turnId);

        var result = await registry.RequestCancelAsync(
            turnId,
            TimeSpan.FromMilliseconds(25));

        result.Should().Be(StreamCancellationResult.StillRunning);
        registry.IsActive(turnId).Should().BeTrue();

        registry.Unregister(turnId);
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
