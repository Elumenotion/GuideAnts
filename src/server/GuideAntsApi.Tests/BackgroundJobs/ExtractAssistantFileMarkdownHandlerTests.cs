using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class ExtractAssistantFileMarkdownHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_Returns_false_when_shadow_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-assistant-missing-{Guid.NewGuid():N}");
        var handler = new ExtractAssistantFileMarkdownHandler(
            NullLogger<ExtractAssistantFileMarkdownHandler>.Instance,
            new Mock<IDocumentIntelligenceService>().Object,
            Microsoft.Extensions.Options.Options.Create(new MarkdownExtractionOptions()),
            BackgroundJobTestHelpers.CreateFactory(options),
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));

        var success = await handler.HandleAsync(new ExtractAssistantFileMarkdownJob(Guid.NewGuid()), CancellationToken.None);

        success.Should().BeFalse();
    }

    [TestMethod]
    public async Task HandleAsync_Returns_true_when_shadow_already_completed()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-assistant-done-{Guid.NewGuid():N}");
        var assistantFileId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            var assistant = new Assistant
            {
                Id = Guid.NewGuid(),
                Name = "Assistant",
                Created = DateTime.UtcNow
            };
            seed.Assistants.Add(assistant);
            seed.AssistantFiles.Add(new AssistantFile
            {
                Id = assistantFileId,
                AssistantId = assistant.Id,
                FolderKind = "VectorStore",
                RelativePath = "guide.md",
                Created = DateTime.UtcNow
            });
            seed.AssistantFileMarkdownShadows.Add(new AssistantFileMarkdownShadow
            {
                OriginalAssistantFileId = assistantFileId,
                ContentHash = "done",
                StoragePath = "done.md",
                FileSize = 1,
                Status = MarkdownExtractionStatus.Completed
            });
            await seed.SaveChangesAsync();
        }

        var docIntel = new Mock<IDocumentIntelligenceService>();
        var handler = new ExtractAssistantFileMarkdownHandler(
            NullLogger<ExtractAssistantFileMarkdownHandler>.Instance,
            docIntel.Object,
            Microsoft.Extensions.Options.Options.Create(new MarkdownExtractionOptions()),
            BackgroundJobTestHelpers.CreateFactory(options),
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));

        var success = await handler.HandleAsync(new ExtractAssistantFileMarkdownJob(assistantFileId), CancellationToken.None);

        success.Should().BeTrue();
        docIntel.VerifyNoOtherCalls();
    }
}
