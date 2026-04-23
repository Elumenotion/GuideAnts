using FluentAssertions;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class LlamaRuntimeCoordinatorTests
{
    [TestMethod]
    public async Task AcquireAliasLockAsync_SerializesOperationsOnSameAlias()
    {
        var coordinator = new LlamaRuntimeCoordinator();
        var firstEntered = new TaskCompletionSource();
        var allowFirstToExit = new TaskCompletionSource();
        var secondEntered = new TaskCompletionSource();

        var firstHolder = Task.Run(async () =>
        {
            await using var _ = await coordinator.AcquireAliasLockAsync("qwen-env");
            firstEntered.SetResult();
            await allowFirstToExit.Task;
        });

        await firstEntered.Task;

        var secondHolder = Task.Run(async () =>
        {
            await using var _ = await coordinator.AcquireAliasLockAsync("qwen-env");
            secondEntered.SetResult();
        });

        var completed = await Task.WhenAny(secondEntered.Task, Task.Delay(250));
        completed.Should().NotBe(secondEntered.Task, "second holder must block while first still holds the alias lock");

        allowFirstToExit.SetResult();

        await firstHolder;
        await secondHolder;
        secondEntered.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [TestMethod]
    public async Task AcquireAliasLockAsync_DoesNotBlockDifferentAliases()
    {
        var coordinator = new LlamaRuntimeCoordinator();

        await using var holdA = await coordinator.AcquireAliasLockAsync("alias-a");

        var acquireB = coordinator.AcquireAliasLockAsync("alias-b");
        var race = await Task.WhenAny(acquireB, Task.Delay(250));
        race.Should().Be(acquireB, "distinct alias locks must not serialize with each other");

        await using var holdB = await acquireB;
    }

    [TestMethod]
    public async Task AcquireAliasLockAsync_ThrowsOnEmptyAlias()
    {
        var coordinator = new LlamaRuntimeCoordinator();
        Func<Task> act = async () => await coordinator.AcquireAliasLockAsync("   ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task AcquireAliasLockAsync_ReleaseIsIdempotent()
    {
        var coordinator = new LlamaRuntimeCoordinator();
        var releaser = await coordinator.AcquireAliasLockAsync("alias-x");
        await releaser.DisposeAsync();
        await releaser.DisposeAsync();

        await using var _next = await coordinator.AcquireAliasLockAsync("alias-x");
    }

    // Phase F (R-6.10 / R-7.1) — non-blocking lock acquisition + diagnostic
    // lock-state read, used by the llama runtime load endpoint to emit 409 and
    // by the status endpoint to render "in progress" without mutating state.

    [TestMethod]
    public void TryAcquireAliasLock_ReturnsNull_WhenAliasIsAlreadyHeld()
    {
        var coordinator = new LlamaRuntimeCoordinator();
        var first = coordinator.TryAcquireAliasLock("alias-busy");
        first.Should().NotBeNull();

        var second = coordinator.TryAcquireAliasLock("alias-busy");
        second.Should().BeNull("a second attempt on the same alias must not block the caller");
    }

    [TestMethod]
    public async Task TryAcquireAliasLock_ReAcquiresAfterReleaseOnSameAlias()
    {
        var coordinator = new LlamaRuntimeCoordinator();
        var handle = coordinator.TryAcquireAliasLock("alias-cycle");
        handle.Should().NotBeNull();
        await handle!.DisposeAsync();

        var reacquired = coordinator.TryAcquireAliasLock("alias-cycle");
        reacquired.Should().NotBeNull("releasing the handle must return the alias to the free pool");
        await reacquired!.DisposeAsync();
    }

    [TestMethod]
    public void TryAcquireAliasLock_DoesNotBlockDifferentAliases()
    {
        var coordinator = new LlamaRuntimeCoordinator();
        var a = coordinator.TryAcquireAliasLock("alias-a");
        var b = coordinator.TryAcquireAliasLock("alias-b");
        a.Should().NotBeNull();
        b.Should().NotBeNull();
    }

    [TestMethod]
    public void TryAcquireAliasLock_ThrowsOnEmptyAlias()
    {
        var coordinator = new LlamaRuntimeCoordinator();
        Action act = () => coordinator.TryAcquireAliasLock("   ");
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void IsAliasLocked_ReturnsFalse_WhenNeverTouched()
    {
        var coordinator = new LlamaRuntimeCoordinator();
        coordinator.IsAliasLocked("never-seen").Should().BeFalse();
    }

    [TestMethod]
    public async Task IsAliasLocked_ReflectsCurrentLockState()
    {
        var coordinator = new LlamaRuntimeCoordinator();
        var handle = await coordinator.AcquireAliasLockAsync("alias-monitored");

        coordinator.IsAliasLocked("alias-monitored").Should().BeTrue();
        coordinator.IsAliasLocked("other-alias").Should().BeFalse();

        await handle.DisposeAsync();

        coordinator.IsAliasLocked("alias-monitored").Should().BeFalse(
            "releasing the handle must flip the lock-state back to free");
    }

    [TestMethod]
    public void IsAliasLocked_ReturnsFalse_ForEmptyAlias()
    {
        // The diagnostic reader must never throw — empty alias is treated as
        // "not a real alias, not locked" so the endpoint aggregator is safe.
        var coordinator = new LlamaRuntimeCoordinator();
        coordinator.IsAliasLocked("").Should().BeFalse();
        coordinator.IsAliasLocked("   ").Should().BeFalse();
    }
}
