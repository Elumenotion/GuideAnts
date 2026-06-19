using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Components;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class AttachmentMessageBuilderTests
{
  private static readonly byte[] TinyPng = Convert.FromBase64String(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO0K2B0AAAAASUVORK5CYII=");

  [TestMethod]
  public void ResizeImageIfNeeded_Returns_original_bytes_for_valid_small_png()
  {
    var resized = AttachmentMessageBuilder.ResizeImageIfNeeded(TinyPng, "image/png");

    resized.Should().Equal(TinyPng);
  }

  [TestMethod]
  public void ResizeImageIfNeeded_Returns_original_bytes_for_invalid_image_data()
  {
    var invalid = new byte[] { 1, 2, 3, 4 };

    var resized = AttachmentMessageBuilder.ResizeImageIfNeeded(invalid, "image/png");

    resized.Should().Equal(invalid);
  }

  [TestMethod]
  public async Task CreateMessagesFromNotebookFileAsync_Inlines_text_file_content()
  {
    var fileId = Guid.NewGuid();
    var notebookFile = new NotebookFile
    {
      Id = fileId,
      RelativePath = "Output/notes.txt",
      NotebookId = Guid.NewGuid()
    };
    var fileService = new Mock<INotebookFileService>();
    fileService
      .Setup(s => s.GetFileContentStreamAsync(fileId, It.IsAny<CancellationToken>()))
      .ReturnsAsync((new MemoryStream("hello attachment"u8.ToArray()), "text/plain", "notes.txt"));

    var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
      notebookFile,
      fileService.Object,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None);

    messages.Should().HaveCountGreaterThanOrEqualTo(2);
    messages[0].Content.First().Text.Should().Contain("notes.txt");
    messages.Should().Contain(m => m.Content.Any(c => c.Text != null && c.Text.Contains("hello attachment")));
  }

  [TestMethod]
  public async Task CreateMessagesFromNotebookFileAsync_Adds_image_content_for_png()
  {
    var fileId = Guid.NewGuid();
    var notebookFile = new NotebookFile
    {
      Id = fileId,
      RelativePath = "Output/photo.png",
      NotebookId = Guid.NewGuid()
    };
    var fileService = new Mock<INotebookFileService>();
    fileService
      .Setup(s => s.GetFileContentStreamAsync(fileId, It.IsAny<CancellationToken>()))
      .ReturnsAsync((new MemoryStream(TinyPng), "image/png", "photo.png"));

    var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
      notebookFile,
      fileService.Object,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None);

    messages.Should().Contain(m =>
      m.Content.Any(c => c.ImageUrl != null && c.ImageUrl.Url.StartsWith("data:image/png;base64,")));
  }

  [TestMethod]
  public async Task CreateMessagesFromNotebookFileAsync_Returns_empty_when_file_service_missing()
  {
    var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
      new NotebookFile { Id = Guid.NewGuid(), RelativePath = "data.csv", NotebookId = Guid.NewGuid() },
      notebookFileService: null,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None);

    messages.Should().BeEmpty();
  }

  [TestMethod]
  public async Task CreateMessagesFromNotebookFileAsync_Uses_relative_path_for_non_output_files()
  {
    var fileId = Guid.NewGuid();
    var notebookFile = new NotebookFile
    {
      Id = fileId,
      RelativePath = "data/report.csv",
      NotebookId = Guid.NewGuid()
    };
    var fileService = new Mock<INotebookFileService>();
    fileService
      .Setup(s => s.GetFileContentStreamAsync(fileId, It.IsAny<CancellationToken>()))
      .ReturnsAsync((new MemoryStream("a,b"u8.ToArray()), "text/csv", "report.csv"));

    var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
      notebookFile,
      fileService.Object,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None);

    messages[0].Content.First().Text.Should().Contain("../data/report.csv");
  }

  [TestMethod]
  public async Task CreateMessagesFromNotebookFileAsync_Uses_path_reference_when_text_exceeds_inline_limit()
  {
    var fileId = Guid.NewGuid();
    var oversizedText = new string('x', 64);
    var notebookFile = new NotebookFile
    {
      Id = fileId,
      RelativePath = "Output/large.txt",
      NotebookId = Guid.NewGuid()
    };
    var fileService = new Mock<INotebookFileService>();
    fileService
      .Setup(s => s.GetFileContentStreamAsync(fileId, It.IsAny<CancellationToken>()))
      .ReturnsAsync((new MemoryStream(System.Text.Encoding.UTF8.GetBytes(oversizedText)), "text/plain", "large.txt"));

    var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
      notebookFile,
      fileService.Object,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None,
      maxInlineMarkdownCharacters: 10);

    messages.Should().Contain(m => m.Content.Any(c => c.Text != null && c.Text.Contains("too large to include inline")));
    messages.Should().NotContain(m => m.Content.Any(c => c.Text != null && c.Text.Contains(oversizedText)));
  }

  [TestMethod]
  public async Task CreateContentFromNotebookFileAsync_Uses_path_reference_when_text_exceeds_inline_limit()
  {
    var fileId = Guid.NewGuid();
    var oversizedText = new string('y', 64);
    var notebookFile = new NotebookFile
    {
      Id = fileId,
      RelativePath = "Output/large-content.txt",
      NotebookId = Guid.NewGuid()
    };
    var fileService = new Mock<INotebookFileService>();
    fileService
      .Setup(s => s.GetFileContentStreamAsync(fileId, It.IsAny<CancellationToken>()))
      .ReturnsAsync((new MemoryStream(System.Text.Encoding.UTF8.GetBytes(oversizedText)), "text/plain", "large-content.txt"));

    var contents = await AttachmentMessageBuilder.CreateContentFromNotebookFileAsync(
      notebookFile,
      fileService.Object,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None,
      maxInlineMarkdownCharacters: 10);

    contents.Should().Contain(c => c.Text != null && c.Text.Contains("too large to include inline"));
    contents.Should().NotContain(c => c.Text != null && c.Text.Contains(oversizedText));
  }

  [TestMethod]
  public async Task CreateMessagesFromNotebookFileAsync_Inlines_unknown_extension_when_payload_is_utf8_text()
  {
    var fileId = Guid.NewGuid();
    const string payload = "hello from unknown extension";
    var notebookFile = new NotebookFile
    {
      Id = fileId,
      RelativePath = "Output/notes.custom",
      NotebookId = Guid.NewGuid()
    };
    var fileService = new Mock<INotebookFileService>();
    fileService
      .Setup(s => s.GetFileContentStreamAsync(fileId, It.IsAny<CancellationToken>()))
      .ReturnsAsync((new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload)), "application/octet-stream", "notes.custom"));

    var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
      notebookFile,
      fileService.Object,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None,
      maxInlineMarkdownCharacters: 1000);

    messages.Should().Contain(m => m.Content.Any(c => c.Text != null && c.Text.Contains(payload)));
  }

  [TestMethod]
  public async Task CreateMessagesFromNotebookFileAsync_Does_not_inline_unknown_extension_when_payload_is_binary()
  {
    var fileId = Guid.NewGuid();
    var notebookFile = new NotebookFile
    {
      Id = fileId,
      RelativePath = "Output/blob.bin",
      NotebookId = Guid.NewGuid()
    };
    var binaryBytes = new byte[] { 0x00, 0xFF, 0x00, 0xAB, 0xCD, 0xEF };
    var fileService = new Mock<INotebookFileService>();
    fileService
      .Setup(s => s.GetFileContentStreamAsync(fileId, It.IsAny<CancellationToken>()))
      .ReturnsAsync((new MemoryStream(binaryBytes), "application/octet-stream", "blob.bin"));

    var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
      notebookFile,
      fileService.Object,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None,
      maxInlineMarkdownCharacters: 1000);

    messages.Should().HaveCount(1);
    messages[0].Content.First().Text.Should().Contain("Attachment: blob.bin");
    messages.Should().NotContain(m => m.Content.Any(c => c.Text != null && c.Text.Contains("file contains")));
  }

  [TestMethod]
  public async Task CreateMessagesFromNotebookFileAsync_Inlines_unknown_extension_when_payload_has_utf16_bom()
  {
    var fileId = Guid.NewGuid();
    const string payload = "utf16 text";
    var notebookFile = new NotebookFile
    {
      Id = fileId,
      RelativePath = "Output/utf16.data",
      NotebookId = Guid.NewGuid()
    };
    var utf16 = new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
    var bom = utf16.GetPreamble();
    var textBytes = utf16.GetBytes(payload);
    var bytes = new byte[bom.Length + textBytes.Length];
    Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
    Buffer.BlockCopy(textBytes, 0, bytes, bom.Length, textBytes.Length);

    var fileService = new Mock<INotebookFileService>();
    fileService
      .Setup(s => s.GetFileContentStreamAsync(fileId, It.IsAny<CancellationToken>()))
      .ReturnsAsync((new MemoryStream(bytes), "application/octet-stream", "utf16.data"));

    var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
      notebookFile,
      fileService.Object,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None,
      maxInlineMarkdownCharacters: 1000);

    messages.Should().Contain(m => m.Content.Any(c => c.Text != null && c.Text.Contains(payload)));
  }

  [TestMethod]
  public async Task CreateMessagesFromNotebookFileAsync_Uses_path_reference_for_unknown_extension_when_text_exceeds_limit()
  {
    var fileId = Guid.NewGuid();
    var oversizedText = new string('z', 64);
    var notebookFile = new NotebookFile
    {
      Id = fileId,
      RelativePath = "Output/oversized.custom",
      NotebookId = Guid.NewGuid()
    };
    var fileService = new Mock<INotebookFileService>();
    fileService
      .Setup(s => s.GetFileContentStreamAsync(fileId, It.IsAny<CancellationToken>()))
      .ReturnsAsync((new MemoryStream(System.Text.Encoding.UTF8.GetBytes(oversizedText)), "application/octet-stream", "oversized.custom"));

    var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
      notebookFile,
      fileService.Object,
      markdownExtractionService: null,
      storagePath: "/storage",
      CancellationToken.None,
      maxInlineMarkdownCharacters: 10);

    messages.Should().Contain(m => m.Content.Any(c => c.Text != null && c.Text.Contains("too large to include inline")));
    messages.Should().NotContain(m => m.Content.Any(c => c.Text != null && c.Text.Contains(oversizedText)));
  }
}
