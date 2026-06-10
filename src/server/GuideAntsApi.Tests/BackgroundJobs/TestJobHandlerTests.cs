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

        var success = await handler.HandleAsync(new TestJob("hello", DelaySeconds: 0), CancellationToken.None);

        success.Should().BeTrue();
        handler.JobType.Should().Be("Test");
    }
}
