using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

/// <summary>
/// Integration coverage for <see cref="AttachmentMessageBuilder"/> exercised against a REAL
/// <see cref="INotebookFileService"/> and an actual file written to the notebook filesystem,
/// validating the file-read + message/content construction round-trip end to end.
/// </summary>
[TestClass]
public sealed class AttachmentMessageBuilderIntegrationTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        SetupAuthentication();
    }

    private async Task<(Guid projectId, Guid notebookId)> CreateProjectAndNotebookAsync()
    {
        var projectResp = await Client.PostAsJsonAsync("/api/projects", new { title = "Attachment Project", description = "d" });
        projectResp.EnsureSuccessStatusCode();
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectDto>();

        var guideId = await GetDefaultGuideIdAsync();
        var notebookResp = await Client.PostAsJsonAsync($"/api/projects/{project!.Id}/notebooks", new { title = "Attachment Notebook", guideId });
        notebookResp.EnsureSuccessStatusCode();
        var notebook = await notebookResp.Content.ReadFromJsonAsync<NotebookDto>();
        return (project.Id, notebook!.Id);
    }

    [TestMethod]
    public async Task CreateMessages_and_content_inline_real_text_file_from_file_service()
    {
        var (projectId, notebookId) = await CreateProjectAndNotebookAsync();

        using var scope = SharedFactory!.Services.CreateScope();
        var fileService = scope.ServiceProvider.GetRequiredService<INotebookFileService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var storagePath = config["FileStorage:Path"] ?? string.Empty;

        const string content = "real attachment text body";
        var created = await fileService.CreateTextFileAsync(projectId, notebookId, "Output/notes.txt", content);
        created.Should().NotBeNull();

        var notebookFile = await db.NotebookFiles.FirstAsync(f => f.Id == created.Id);

        var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
            notebookFile,
            fileService,
            markdownExtractionService: null,
            storagePath: storagePath,
            CancellationToken.None);

        messages.Should().HaveCountGreaterThanOrEqualTo(2);
        // Rule 1: path is always added (Output/ files use bare filename)
        messages[0].Content.First().Text.Should().Contain("notes.txt");
        // Rule 4: known text extension content inlined
        messages.Should().Contain(m => m.Content.Any(c => c.Text != null && c.Text.Contains(content)));

        var contents = await AttachmentMessageBuilder.CreateContentFromNotebookFileAsync(
            notebookFile,
            fileService,
            markdownExtractionService: null,
            storagePath: storagePath,
            CancellationToken.None);

        contents.Should().Contain(c => c.Text != null && c.Text.Contains("notes.txt"));
        contents.Should().Contain(c => c.Text != null && c.Text.Contains(content));
    }

    [TestMethod]
    public async Task CreateMessages_returns_empty_when_file_service_is_null()
    {
        var (_, notebookId) = await CreateProjectAndNotebookAsync();

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var notebookFile = new GuideAntsApi.DataModel.Models.NotebookFile
        {
            Id = Guid.NewGuid(),
            NotebookId = notebookId,
            RelativePath = "data/report.csv",
            FileSize = 4,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "h"
        };
        notebookFile.GenerateDocumentId(notebookId);

        var messages = await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
            notebookFile,
            notebookFileService: null,
            markdownExtractionService: null,
            storagePath: "/storage",
            CancellationToken.None);

        messages.Should().BeEmpty();
    }
}
