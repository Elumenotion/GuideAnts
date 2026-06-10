using AntRunner.Chat.Abstractions;
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
}
