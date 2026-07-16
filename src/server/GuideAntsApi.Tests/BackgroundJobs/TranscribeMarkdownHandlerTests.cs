using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class TranscribeMarkdownHandlerTests
{
    [TestMethod]
    public async Task TranscribeNotebookFileMarkdownHandler_Returns_false_when_shadow_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-nb-missing-{Guid.NewGuid():N}");
        var handler = new TranscribeNotebookFileMarkdownHandler(
            NullLogger<TranscribeNotebookFileMarkdownHandler>.Instance,
            BackgroundJobTestHelpers.CreateFactory(options),
            new Mock<ITranscriptionAdapter>().Object,
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));

        var result = await handler.HandleAsync(new TranscribeNotebookFileMarkdownJob(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
    }

    [TestMethod]
    public async Task TranscribeContentVersionMarkdownHandler_Returns_false_when_shadow_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"transcribe-content-missing-{Guid.NewGuid():N}");
        var handler = new TranscribeContentVersionMarkdownHandler(
            NullLogger<TranscribeContentVersionMarkdownHandler>.Instance,
            BackgroundJobTestHelpers.CreateFactory(options),
            new Mock<ITranscriptionAdapter>().Object,
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));

        var result = await handler.HandleAsync(new TranscribeContentVersionMarkdownJob(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureClass.Should().Be(JobFailureClass.PermanentMissingInput);
    }

}
