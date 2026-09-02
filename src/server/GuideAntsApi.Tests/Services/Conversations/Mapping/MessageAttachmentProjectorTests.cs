using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Mapping;

namespace GuideAntsApi.Tests.Services.Conversations.Mapping;

[TestClass]
public sealed class MessageAttachmentProjectorTests
{
    [TestMethod]
    public void Projects_path_only_folder_with_normalized_path_and_folder_file_type()
    {
        var dto = MessageAttachmentProjector.ToAttachedFileDto(
            notebookFileId: null,
            relativePath: " \\Assets\\Reference Pack ",
            notebookFileRelativePath: null,
            uploadType: ContentUploadType.Folder,
            fileSize: 0,
            type: AttachmentType.Referenced);

        dto.NotebookFileId.Should().BeNull();
        dto.RelativePath.Should().Be("Assets/Reference Pack");
        dto.UploadType.Should().Be(ContentUploadType.Folder);
        dto.FileName.Should().Be("Reference Pack");
        dto.FileType.Should().Be("folder");
    }

    [TestMethod]
    public void Projects_file_backed_attachment_from_notebook_file_path()
    {
        var dto = MessageAttachmentProjector.ToAttachedFileDto(
            notebookFileId: Guid.NewGuid(),
            relativePath: null,
            notebookFileRelativePath: "Output/notes.md",
            uploadType: ContentUploadType.TextFile,
            fileSize: 42,
            type: AttachmentType.Referenced);

        dto.RelativePath.Should().Be("Output/notes.md");
        dto.FileName.Should().Be("notes.md");
        dto.FileType.Should().Be("text");
        dto.FileSize.Should().Be(42);
    }

    [TestMethod]
    public void Uses_extension_inference_for_legacy_null_upload_type()
    {
        var dto = MessageAttachmentProjector.ToAttachedFileDto(
            notebookFileId: Guid.NewGuid(),
            relativePath: null,
            notebookFileRelativePath: "Output/legacy.png",
            uploadType: null,
            fileSize: 1,
            type: AttachmentType.Referenced);

        dto.UploadType.Should().BeNull();
        dto.FileType.Should().Be("image");
    }
}
