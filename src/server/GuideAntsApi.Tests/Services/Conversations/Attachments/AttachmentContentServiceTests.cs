using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Tests.Services.Conversations.Attachments;

[TestClass]
public sealed class AttachmentContentServiceTests
{
    [TestMethod]
    public async Task Persists_resolved_and_path_only_attachments_with_canonical_deduplication()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"attachments-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var notebookId = Guid.NewGuid();
        var notebookFileId = Guid.NewGuid();
        db.NotebookFiles.Add(new NotebookFile
        {
            Id = notebookFileId,
            NotebookId = notebookId,
            RelativePath = "Output/Report.CSV",
            FileSize = 123,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash",
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var messageId = Guid.NewGuid();
        await service.AddAttachmentsToUserMessageAsync(
            db,
            messageId,
            notebookId,
            new[]
            {
                new AttachmentDto(notebookFileId, ContentUploadType.TextFile),
                new AttachmentDto(null, ContentUploadType.TextFile, " \\output\\report.csv "),
                new AttachmentDto(null, ContentUploadType.SandboxFile, " /Host/Mount.txt "),
                new AttachmentDto(null, ContentUploadType.SandboxFile, "host\\mount.txt"),
                new AttachmentDto(null, ContentUploadType.Folder, "Collections\\Pack")
            });

        var rows = db.MessageAttachments
            .Where(a => a.MessageId == messageId)
            .OrderBy(a => a.OrderIndex)
            .ToList();

        rows.Should().HaveCount(3);
        rows[0].NotebookFileId.Should().Be(notebookFileId);
        rows[0].RelativePath.Should().BeNull();
        rows[0].UploadType.Should().Be(ContentUploadType.TextFile);
        rows[0].OrderIndex.Should().Be(0);
        rows[1].NotebookFileId.Should().BeNull();
        rows[1].RelativePath.Should().Be("Host/Mount.txt");
        rows[1].UploadType.Should().Be(ContentUploadType.SandboxFile);
        rows[1].OrderIndex.Should().Be(1);
        rows[2].NotebookFileId.Should().BeNull();
        rows[2].RelativePath.Should().Be("Collections/Pack");
        rows[2].UploadType.Should().Be(ContentUploadType.Folder);
        rows[2].OrderIndex.Should().Be(2);
    }

    [TestMethod]
    public async Task Expands_path_only_file_and_folder_to_cwd_relative_notices()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"attachment-expand-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var service = CreateService(db);

        var fileContents = await service.ExpandAttachmentToChatContentsAsync(
            db,
            new MessageAttachment
            {
                RelativePath = "Data\\file.csv",
                UploadType = ContentUploadType.TextFile
            });
        var folderContents = await service.ExpandAttachmentToChatContentsAsync(
            db,
            new MessageAttachment
            {
                RelativePath = "Assets\\reference-pack",
                UploadType = ContentUploadType.Folder
            });

        fileContents.Select(c => c.Text).Should().ContainSingle("Attachment: ../Data/file.csv", "path-only files use the notebook-to-Output path");
        folderContents.Select(c => c.Text).Should().ContainSingle("Attachment (folder): ../Assets/reference-pack");
    }

    private static AttachmentContentService CreateService(ApplicationDbContext db) =>
        new(
            new TestServiceScopeFactory(db),
            Microsoft.Extensions.Options.Options.Create(new MarkdownAttachmentOptions()),
            logger: NullLogger<AttachmentContentService>.Instance);
}
