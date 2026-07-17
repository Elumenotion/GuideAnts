using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class TestJobHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_Completes_without_delay()
    {
        var handler = new TestJobHandler(NullLogger<TestJobHandler>.Instance);

        var result = await handler.HandleAsync(new TestJob("hello", DelaySeconds: 0), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        handler.JobType.Should().Be("Test");
    }

    [TestMethod]
    public async Task HandleAsync_Completes_with_positive_delay()
    {
        var handler = new TestJobHandler(NullLogger<TestJobHandler>.Instance);

        var result = await handler.HandleAsync(new TestJob("hello", DelaySeconds: 1), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [TestMethod]
    public async Task HandleAsync_WhenCancelledDuringDelay_ThrowsOperationCanceledException()
    {
        var handler = new TestJobHandler(NullLogger<TestJobHandler>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await handler.HandleAsync(new TestJob("hello", DelaySeconds: 5), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
