using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
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

        var success = await handler.HandleAsync(new RetentionCleanupJob(Guid.NewGuid()), CancellationToken.None);

        success.Should().BeTrue();
    }

}
