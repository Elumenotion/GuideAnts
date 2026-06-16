using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class ExtractContentVersionMarkdownHandlerTests
{
    [TestMethod]
    public async Task HandleAsync_Returns_false_when_shadow_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-content-missing-{Guid.NewGuid():N}");
        var handler = new ExtractContentVersionMarkdownHandler(
            NullLogger<ExtractContentVersionMarkdownHandler>.Instance,
            new Mock<IDocumentIntelligenceService>().Object,
            Microsoft.Extensions.Options.Options.Create(new MarkdownExtractionOptions()),
            BackgroundJobTestHelpers.CreateFactory(options),
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));

        var success = await handler.HandleAsync(new ExtractContentVersionMarkdownJob(Guid.NewGuid()), CancellationToken.None);

        success.Should().BeFalse();
    }

    [TestMethod]
    public async Task HandleAsync_Returns_true_when_shadow_already_completed()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"extract-content-done-{Guid.NewGuid():N}");
        var versionId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            var (projectId, _) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            var contentFile = new ContentFile
            {
                ProjectId = projectId,
                FileName = "doc.pdf",
                Path = "doc.pdf",
                RelativePath = "doc.pdf",
                FileSize = 1,
                ContentType = "application/pdf",
                Created = DateTime.UtcNow
            };
            contentFile.GenerateDocumentId();
            seed.ContentFiles.Add(contentFile);
            var version = new ContentFileVersion
            {
                Id = versionId,
                ContentFileId = contentFile.Id,
                VersionNumber = 1,
                FileName = "doc.pdf",
                Path = "doc.pdf",
                RelativePath = "doc.pdf",
                StoragePath = "doc.pdf",
                FileSize = 1,
                ContentType = "application/pdf",
                Indexed = false,
                Created = DateTime.UtcNow
            };
            seed.ContentFileVersions.Add(version);
            seed.ContentFileMarkdownShadows.Add(new ContentFileMarkdownShadow
            {
                OriginalContentFileVersionId = versionId,
                ContentHash = "done",
                StoragePath = "done.md",
                FileSize = 1,
                Status = MarkdownExtractionStatus.Completed
            });
            await seed.SaveChangesAsync();
        }

        var docIntel = new Mock<IDocumentIntelligenceService>();
        var handler = new ExtractContentVersionMarkdownHandler(
            NullLogger<ExtractContentVersionMarkdownHandler>.Instance,
            docIntel.Object,
            Microsoft.Extensions.Options.Options.Create(new MarkdownExtractionOptions()),
            BackgroundJobTestHelpers.CreateFactory(options),
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));

        var success = await handler.HandleAsync(new ExtractContentVersionMarkdownJob(versionId), CancellationToken.None);

        success.Should().BeTrue();
        docIntel.VerifyNoOtherCalls();
    }
}
