using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class RetentionCleanupHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_Skips_when_disabled_in_configuration()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"retention-disabled-{Guid.NewGuid():N}");
        var handler = new RetentionCleanupHandler(
            NullLogger<RetentionCleanupHandler>.Instance,
            BackgroundJobTestHelpers.CreateFactory(options),
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath(), retentionEnabled: false));

        var result = await handler.HandleAsync(new RetentionCleanupJob(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

}
