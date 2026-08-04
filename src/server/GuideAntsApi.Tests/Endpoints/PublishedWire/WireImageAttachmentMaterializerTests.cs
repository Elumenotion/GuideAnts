using FluentAssertions;
using GuideAntsApi.Endpoints.PublishedWire;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Components;
using Microsoft.AspNetCore.Http;
using Moq;

namespace GuideAntsApi.Tests.Endpoints.PublishedWire;

[TestClass]
public sealed class WireImageAttachmentMaterializerTests
{
    [TestMethod]
    public async Task MaterializeAsync_Uploads_data_uri_as_notebook_image_attachment()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var notebookFileService = new Mock<INotebookFileService>(MockBehavior.Strict);
        notebookFileService
            .Setup(s => s.UploadFilesAsync(
                projectId,
                notebookId,
                It.IsAny<IFormFileCollection>(),
                "Output/.wire-attachments",
                false,
                false))
            .ReturnsAsync(
            [
                new NotebookFileDto(
                    fileId,
                    "wire-image.png",
                    "Output/.wire-attachments/wire-image.png",
                    4,
                    DateTime.UtcNow,
                    "hash",
                    null,
                    false,
                    false)
            ]);

        var attachments = await WireImageAttachmentMaterializer.MaterializeAsync(
            notebookFileService.Object,
            projectId,
            notebookId,
            ["data:image/png;base64,AAAA"]);

        attachments.Should().ContainSingle();
        attachments[0].NotebookFileId.Should().Be(fileId);
        attachments[0].UploadType.Should().Be(ContentUploadType.ImageFile);
        notebookFileService.VerifyAll();
    }

    [TestMethod]
    public async Task MaterializeAsync_Skips_invalid_data_uri()
    {
        var notebookFileService = new Mock<INotebookFileService>(MockBehavior.Strict);

        var attachments = await WireImageAttachmentMaterializer.MaterializeAsync(
            notebookFileService.Object,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ["data:image/png;base64,!!!not-base64!!!"]);

        attachments.Should().BeEmpty();
    }
}
