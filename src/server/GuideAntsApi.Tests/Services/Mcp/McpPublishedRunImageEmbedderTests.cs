using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpPublishedRunImageEmbedderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static ApplicationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static McpPublishedRunImageEmbedder CreateEmbedder(
        ApplicationDbContext db,
        INotebookFileService fileService,
        McpImageEmbeddingOptions? options = null) =>
        new(
            db,
            fileService,
            Microsoft.Extensions.Options.Options.Create(options ?? new McpImageEmbeddingOptions()),
            NullLogger<McpPublishedRunImageEmbedder>.Instance);

    private static NotebookFile AddFile(
        ApplicationDbContext db,
        Guid notebookId,
        string relativePath,
        DateTime? lastModified = null)
    {
        var file = new NotebookFile
        {
            Id = Guid.NewGuid(),
            NotebookId = notebookId,
            RelativePath = relativePath,
            FileSize = 1,
            LastModifiedUtc = lastModified ?? DateTime.UtcNow,
            FileHash = "hash"
        };
        file.GenerateDocumentId(notebookId);
        db.NotebookFiles.Add(file);
        return file;
    }

    private static void AddTurn(
        ApplicationDbContext db,
        Guid conversationId,
        int turnIndex,
        IEnumerable<string>? created = null,
        IEnumerable<string>? modified = null)
    {
        db.ConversationTurns.Add(new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            TurnIndex = turnIndex,
            AssistantName = "Media_Creator",
            Instructions = "make a dolphin",
            FilesCreated = created != null ? JsonSerializer.Serialize(created.ToList(), JsonOptions) : null,
            FilesModified = modified != null ? JsonSerializer.Serialize(modified.ToList(), JsonOptions) : null
        });
    }

    private static Mock<INotebookFileService> CreateFileServiceMock(
        IDictionary<Guid, (byte[] Bytes, string ContentType)> files)
    {
        var mock = new Mock<INotebookFileService>();
        mock.Setup(s => s.GetFileContentStreamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
            {
                if (!files.TryGetValue(id, out var entry))
                {
                    return null;
                }

                return (new MemoryStream(entry.Bytes) as Stream, entry.ContentType, "file");
            });
        return mock;
    }

    [TestMethod]
    public async Task EmbedTurnImagesAsync_Embeds_created_png_from_run_folder()
    {
        await using var db = CreateContext();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var file = AddFile(db, notebookId, "Runs/xrNV7kuljr/friendly_dolphin.png");
        AddTurn(db, conversationId, 1, created: ["friendly_dolphin.png"]);
        await db.SaveChangesAsync();

        var bytes = Encoding.UTF8.GetBytes("PNGDATA");
        var fileService = CreateFileServiceMock(new Dictionary<Guid, (byte[], string)>
        {
            [file.Id] = (bytes, "image/png")
        });

        var embedder = CreateEmbedder(db, fileService.Object);

        var blocks = await embedder.EmbedTurnImagesAsync(notebookId, conversationId, 1, CancellationToken.None);

        blocks.Should().HaveCount(1);
        blocks[0].MimeType.Should().Be("image/png");
        blocks[0].DecodedData.ToArray().Should().Equal(bytes);
    }

    [TestMethod]
    public async Task EmbedTurnImagesAsync_Skips_non_image_files()
    {
        await using var db = CreateContext();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var script = AddFile(db, notebookId, "Runs/r1/generate.py");
        var notes = AddFile(db, notebookId, "Runs/r1/notes.txt");
        AddTurn(db, conversationId, 1, created: ["generate.py", "notes.txt"]);
        await db.SaveChangesAsync();

        var fileService = CreateFileServiceMock(new Dictionary<Guid, (byte[], string)>
        {
            [script.Id] = (Encoding.UTF8.GetBytes("print('hi')"), "text/x-python"),
            [notes.Id] = (Encoding.UTF8.GetBytes("hello"), "text/plain")
        });

        var embedder = CreateEmbedder(db, fileService.Object);

        var blocks = await embedder.EmbedTurnImagesAsync(notebookId, conversationId, 1, CancellationToken.None);

        blocks.Should().BeEmpty();
    }

    [TestMethod]
    public async Task EmbedTurnImagesAsync_Skips_files_over_size_cap()
    {
        await using var db = CreateContext();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var file = AddFile(db, notebookId, "Runs/r1/big.png");
        AddTurn(db, conversationId, 1, created: ["big.png"]);
        await db.SaveChangesAsync();

        var fileService = CreateFileServiceMock(new Dictionary<Guid, (byte[], string)>
        {
            [file.Id] = (new byte[64], "image/png")
        });

        var embedder = CreateEmbedder(db, fileService.Object, new McpImageEmbeddingOptions { MaxImageBytes = 16 });

        var blocks = await embedder.EmbedTurnImagesAsync(notebookId, conversationId, 1, CancellationToken.None);

        blocks.Should().BeEmpty();
    }

    [TestMethod]
    public async Task EmbedTurnImagesAsync_Caps_count_and_dedupes()
    {
        await using var db = CreateContext();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var files = new Dictionary<Guid, (byte[], string)>();
        var created = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var f = AddFile(db, notebookId, $"Runs/r1/img{i}.png");
            files[f.Id] = (Encoding.UTF8.GetBytes($"img{i}"), "image/png");
            created.Add($"img{i}.png");
        }

        // Duplicate reference to the first image should not double-count.
        created.Add("img0.png");
        AddTurn(db, conversationId, 1, created: created);
        await db.SaveChangesAsync();

        var fileService = CreateFileServiceMock(files);
        var embedder = CreateEmbedder(db, fileService.Object, new McpImageEmbeddingOptions { MaxImagesPerResponse = 5 });

        var blocks = await embedder.EmbedTurnImagesAsync(notebookId, conversationId, 1, CancellationToken.None);

        blocks.Should().HaveCount(5);
    }

    [TestMethod]
    public async Task EmbedTurnImagesAsync_Includes_modified_when_enabled()
    {
        await using var db = CreateContext();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var modified = AddFile(db, notebookId, "Runs/r1/updated.png");
        AddTurn(db, conversationId, 1, created: [], modified: ["updated.png"]);
        await db.SaveChangesAsync();

        var fileService = CreateFileServiceMock(new Dictionary<Guid, (byte[], string)>
        {
            [modified.Id] = (Encoding.UTF8.GetBytes("png"), "image/png")
        });

        var withModified = CreateEmbedder(db, fileService.Object, new McpImageEmbeddingOptions { IncludeModifiedFiles = true });
        var blocksWith = await withModified.EmbedTurnImagesAsync(notebookId, conversationId, 1, CancellationToken.None);
        blocksWith.Should().HaveCount(1);

        var withoutModified = CreateEmbedder(db, fileService.Object, new McpImageEmbeddingOptions { IncludeModifiedFiles = false });
        var blocksWithout = await withoutModified.EmbedTurnImagesAsync(notebookId, conversationId, 1, CancellationToken.None);
        blocksWithout.Should().BeEmpty();
    }

    [TestMethod]
    public async Task EmbedTurnImagesAsync_Returns_empty_when_disabled()
    {
        await using var db = CreateContext();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var file = AddFile(db, notebookId, "Runs/r1/dolphin.png");
        AddTurn(db, conversationId, 1, created: ["dolphin.png"]);
        await db.SaveChangesAsync();

        var fileService = CreateFileServiceMock(new Dictionary<Guid, (byte[], string)>
        {
            [file.Id] = (Encoding.UTF8.GetBytes("png"), "image/png")
        });

        var embedder = CreateEmbedder(db, fileService.Object, new McpImageEmbeddingOptions { EmbedImages = false });

        var blocks = await embedder.EmbedTurnImagesAsync(notebookId, conversationId, 1, CancellationToken.None);

        blocks.Should().BeEmpty();
    }
}
