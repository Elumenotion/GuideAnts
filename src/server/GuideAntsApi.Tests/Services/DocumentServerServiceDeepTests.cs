using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services;

/// <summary>
/// Deep coverage for <see cref="DocumentServerService"/> focused on the non-JWT (query string)
/// token mode, notebook-scope resolution, support gating, and the various validation/error
/// branches not exercised by the base suite. HTTP callbacks are faked via IHttpClientFactory.
/// </summary>
[TestClass]
public sealed class DocumentServerServiceDeepTests
{
    [TestMethod]
    public void IsSupported_RecognizesExtensionAndContentType()
    {
        var service = CreateService(CreateDbContext(), new StubContentFileService(null), enabled: true);

        service.IsSupported("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document").Should().BeTrue();
        service.IsSupported("a.docx", "application/octet-stream").Should().BeTrue();
        service.IsSupported("a.docx", "image/png").Should().BeFalse();
        service.IsSupported("a.xyz", "application/octet-stream").Should().BeFalse();
        service.IsSupported("noext", "application/pdf").Should().BeFalse();
        service.SupportedExtensions.Should().Contain("pdf");
        service.SupportedContentTypes.Should().Contain("application/pdf");
    }

    [TestMethod]
    public async Task BuildEditorConfig_NonJwt_BuildsQueryStringUrls_WithoutToken()
    {
        await using var db = CreateDbContext();
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var service = CreateService(db, new StubContentFileService(Details(fileId, "proposal.docx", latestVersion: 4)), enabled: true, jwtEnabled: false);

        var result = await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest("project", projectId, fileId, null, true, "u", "n"),
            CancellationToken.None);

        var config = (Dictionary<string, object?>)result.Config;
        config.ContainsKey("token").Should().BeFalse();
        var document = (Dictionary<string, object?>)config["document"]!;
        var downloadUrl = document["url"]!.ToString()!;
        downloadUrl.Should().Contain("scope=project");
        downloadUrl.Should().Contain($"fileId={fileId:D}");
        downloadUrl.Should().Contain("versionNumber=4");
    }

    [TestMethod]
    public async Task GetDownload_NonJwt_ReturnsProjectFileContent()
    {
        await using var db = CreateDbContext();
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("the-content");
        var contentService = new StubContentFileService(
            Details(fileId, "proposal.docx", latestVersion: 2),
            new ContentFileContentDto { Content = bytes, ContentType = "application/pdf", FileName = "proposal.docx" });
        var service = CreateService(db, contentService, enabled: true, jwtEnabled: false);

        var result = await service.GetDownloadAsync(null, "project", projectId, fileId, null, null, null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.FileName.Should().Be("proposal.docx");
        contentService.LastRequestedVersionNumber.Should().Be(2);
    }

    [TestMethod]
    public async Task GetDownload_Disabled_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: false, jwtEnabled: false);

        var act = async () => await service.GetDownloadAsync(null, "project", Guid.NewGuid(), Guid.NewGuid(), null, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disabled*");
    }

    [TestMethod]
    public async Task GetDownload_NonJwt_MissingScope_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: false);

        var act = async () => await service.GetDownloadAsync(null, null, Guid.NewGuid(), Guid.NewGuid(), null, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*scope is missing*");
    }

    [TestMethod]
    public async Task GetDownload_NonJwt_MissingIdentity_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: false);

        var act = async () => await service.GetDownloadAsync(null, "project", null, null, null, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*identity is missing*");
    }

    [TestMethod]
    public async Task GetDownload_ProjectFileNotFound_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: false);

        var result = await service.GetDownloadAsync(null, "project", Guid.NewGuid(), Guid.NewGuid(), null, null, null, CancellationToken.None);

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetDownload_ProjectContentMissing_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var fileId = Guid.NewGuid();
        var service = CreateService(db, new StubContentFileService(Details(fileId, "a.pdf", latestVersion: 1)), enabled: true, jwtEnabled: false);

        var result = await service.GetDownloadAsync(null, "project", Guid.NewGuid(), fileId, null, null, null, CancellationToken.None);

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetDownload_UnsupportedScope_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: false);

        var act = async () => await service.GetDownloadAsync(null, "bogus", Guid.NewGuid(), Guid.NewGuid(), null, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Unsupported DocumentServer scope*");
    }

    [TestMethod]
    public async Task GetDownload_NotebookScope_MissingNotebookIdentity_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: false);

        var act = async () => await service.GetDownloadAsync(null, "notebook", Guid.NewGuid(), Guid.NewGuid(), null, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing notebook identity*");
    }

    [TestMethod]
    public async Task GetDownload_NotebookScope_ReturnsNotebookFileContent()
    {
        await using var db = CreateDbContext();
        var notebookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var notebookFileService = new StubNotebookFileService
        {
            FileContentStream = (new MemoryStream(Encoding.UTF8.GetBytes("nb")), "text/plain", "notes.txt")
        };
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: false,
            notebookFileService: notebookFileService);

        var result = await service.GetDownloadAsync(null, "notebook", Guid.NewGuid(), fileId, notebookId, null, null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.FileName.Should().Be("notes.txt");
    }

    [TestMethod]
    public async Task BuildEditorConfig_NotebookScope_ResolvesNotebookFile()
    {
        await using var db = CreateDbContext();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        db.NotebookFiles.Add(new NotebookFile
        {
            Id = fileId,
            NotebookId = notebookId,
            RelativePath = "docs/report.docx",
            FileSize = 10,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: true);

        var result = await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest("notebook", projectId, fileId, notebookId, true, "u", "n"),
            CancellationToken.None);

        var config = (Dictionary<string, object?>)result.Config;
        var document = (Dictionary<string, object?>)config["document"]!;
        document["title"].Should().Be("report.docx");
        document["fileType"].Should().Be("docx");
    }

    [TestMethod]
    public async Task BuildEditorConfig_NotebookScope_RequiresNotebookId()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: true);

        var act = async () => await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest("notebook", Guid.NewGuid(), Guid.NewGuid(), null, true, "u", "n"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*requires notebookId*");
    }

    [TestMethod]
    public async Task BuildEditorConfig_UnsupportedFileType_Throws()
    {
        await using var db = CreateDbContext();
        var fileId = Guid.NewGuid();
        var service = CreateService(db, new StubContentFileService(Details(fileId, "image.png", latestVersion: 1, contentType: "image/png")), enabled: true, jwtEnabled: true);

        var act = async () => await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest("project", Guid.NewGuid(), fileId, null, true, "u", "n"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not supported*");
    }

    [TestMethod]
    public async Task BuildEditorConfig_ProjectFileNotFound_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: true);

        var act = async () => await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest("project", Guid.NewGuid(), Guid.NewGuid(), null, true, "u", "n"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Project file was not found*");
    }

    [TestMethod]
    public async Task BuildEditorConfig_MissingApiBaseUrl_Throws()
    {
        await using var db = CreateDbContext();
        var fileId = Guid.NewGuid();
        var service = CreateService(db, new StubContentFileService(Details(fileId, "a.docx", latestVersion: 1)), enabled: true, jwtEnabled: true, apiBaseUrl: string.Empty);

        var act = async () => await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest("project", Guid.NewGuid(), fileId, null, true, "u", "n"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ApiBaseUrl must be configured*");
    }

    [TestMethod]
    public async Task HandleCallback_Disabled_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: false, jwtEnabled: false);

        var act = async () => await service.HandleCallbackAsync(null, "project", Guid.NewGuid(), Guid.NewGuid(), null, null,
            new DocumentServerCallbackPayload(Status: 2, Url: "http://x/y"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disabled*");
    }

    [TestMethod]
    public async Task HandleCallback_NonJwt_ProjectFileNotFound_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: false,
            httpClientFactory: new StubHttpClientFactory(Encoding.UTF8.GetBytes("data")));

        var act = async () => await service.HandleCallbackAsync(null, "project", Guid.NewGuid(), Guid.NewGuid(), null, null,
            new DocumentServerCallbackPayload(Status: 2, Url: "http://callback.local/file.docx"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Project file not found*");
    }

    [TestMethod]
    public async Task HandleCallback_NotebookScope_FileNotFound_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: false,
            httpClientFactory: new StubHttpClientFactory(Encoding.UTF8.GetBytes("data")));

        var act = async () => await service.HandleCallbackAsync(null, "notebook", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            new DocumentServerCallbackPayload(Status: 2, Url: "http://callback.local/file.docx"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Notebook file not found*");
    }

    [TestMethod]
    public async Task HandleCallback_NonSaveStatusWithoutUrl_Ignored()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(null), enabled: true, jwtEnabled: false);

        var act = async () => await service.HandleCallbackAsync(null, "project", Guid.NewGuid(), Guid.NewGuid(), null, null,
            new DocumentServerCallbackPayload(Status: 2, Url: null), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static ContentFileDetailsDto Details(Guid id, string fileName, int latestVersion, string contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document") =>
        new(
            Id: id,
            FileName: fileName,
            Path: string.Empty,
            RelativePath: fileName,
            ContentType: contentType,
            Index: false,
            DocumentId: "doc-1",
            Created: DateTime.UtcNow,
            FileSize: 42,
            FolderId: null,
            FolderPath: null,
            LatestVersion: latestVersion,
            IsSnapshot: false,
            HasMarkdownShadow: false,
            MarkdownStatus: null,
            MarkdownProcessedAt: null);

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"documentserver-deep-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }

    private static DocumentServerService CreateService(
        ApplicationDbContext db,
        IContentFileService contentFileService,
        bool enabled,
        bool jwtEnabled = true,
        IHttpClientFactory? httpClientFactory = null,
        INotebookFileService? notebookFileService = null,
        string internalUrl = "http://documentserver",
        string apiBaseUrl = "http://host.docker.internal:5106")
    {
        return new DocumentServerService(
            db,
            contentFileService,
            notebookFileService ?? new StubNotebookFileService(),
            Microsoft.Extensions.Options.Options.Create(new DocumentServerOptions
            {
                Enabled = enabled,
                InternalUrl = internalUrl,
                ApiBaseUrl = apiBaseUrl,
                JwtEnabled = jwtEnabled,
                JwtSecret = "documentserver-deep-secret"
            }),
            httpClientFactory ?? new StubHttpClientFactory(),
            NullLogger<DocumentServerService>.Instance);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .BuildServiceProvider()
        };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost:5107");
        return httpContext;
    }

    private sealed class StubHttpClientFactory(byte[]? responseBytes = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            if (responseBytes == null)
            {
                return new HttpClient();
            }

            return new HttpClient(new FixedResponseHandler(responseBytes));
        }
    }

    private sealed class FixedResponseHandler(byte[] responseBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes)
            });
    }

    private sealed class StubContentFileService(ContentFileDetailsDto? details, ContentFileContentDto? versionContent = null) : IContentFileService
    {
        public int? LastRequestedVersionNumber { get; private set; }

        public async Task<ContentFileDetailsDto> UploadFileAsync(Guid projectId, IFormFile file, bool index = false, Guid? folderId = null)
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return details ?? throw new InvalidOperationException("No content file configured.");
        }

        public Task<ContentFileDetailsDto?> MoveFileAsync(Guid projectId, Guid fileId, Guid? destinationFolderId) => Task.FromResult<ContentFileDetailsDto?>(null);
        public Task<bool> DeleteAsync(Guid projectId, Guid fileId) => Task.FromResult(false);
        public Task<ContentFileDetailsDto?> GetAsync(Guid projectId, Guid fileId) => Task.FromResult(details);
        public Task<IEnumerable<ContentFileDetailsDto>> GetAllForProjectAsync(Guid projectId) => Task.FromResult<IEnumerable<ContentFileDetailsDto>>([]);
        public Task<ContentFileDetailsDto?> UpdateAsync(Guid projectId, Guid fileId, UpdateContentFileDto updates) => Task.FromResult<ContentFileDetailsDto?>(null);
        public Task<ContentFileContentDto?> GetContentAsync(Guid projectId, Guid fileId) => Task.FromResult<ContentFileContentDto?>(null);
        public Task<IEnumerable<ContentFileVersionDto>> GetVersionsAsync(Guid projectId, Guid fileId) => Task.FromResult<IEnumerable<ContentFileVersionDto>>([]);
        public Task<ContentFileContentDto?> GetVersionContentAsync(Guid projectId, Guid fileId, int versionNumber)
        {
            LastRequestedVersionNumber = versionNumber;
            return Task.FromResult(versionContent);
        }
        public Task<ContentFileDetailsDto> CreateFileFromPathAsync(Guid projectId, string sourcePath, string fileName, string contentType, Guid? folderId, Guid originNotebookFileId, bool index = false) => Task.FromResult(details!);
        public Task<ContentFileDetailsDto> CreateVersionFromPathAsync(Guid projectId, Guid contentFileId, string sourcePath, Guid originNotebookFileId, bool index = false) => Task.FromResult(details!);
    }

    private sealed class StubNotebookFileService : INotebookFileService
    {
        public (Stream Stream, string ContentType, string FileName)? FileContentStream { get; set; }

        public Task<IEnumerable<NotebookFileDto>> ListFilesAsync(Guid projectId, Guid notebookId) => Task.FromResult<IEnumerable<NotebookFileDto>>([]);
        public Task<NotebookFolderTreeDto?> GetFolderTreeAsync(Guid projectId, Guid notebookId) => Task.FromResult<NotebookFolderTreeDto?>(null);
        public Task<HostMountListingDto?> ListHostMountLevelAsync(Guid projectId, Guid notebookId, string relativePath) => Task.FromResult<HostMountListingDto?>(null);
        public Task<(Stream Stream, string ContentType, string FileName)?> GetFileAsync(Guid projectId, Guid notebookId, string relativePath) => Task.FromResult<(Stream, string, string)?>(null);
        public Task<(Stream stream, string contentType)> GetFileContentStreamAsync(Guid projectId, Guid notebookId, string relativePath) => throw new NotImplementedException();
        public Task<(Stream Stream, string ContentType, string FileName)?> GetFileContentStreamAsync(Guid notebookFileId, CancellationToken cancellationToken = default) => Task.FromResult(FileContentStream);
        public Task<NotebookFileDto?> CopyFromProjectAsync(Guid projectId, Guid notebookId, Guid contentFileId, int? versionNumber, string? targetRelativePath) => Task.FromResult<NotebookFileDto?>(null);
        public Task<IEnumerable<NotebookFileDto>> UploadFilesAsync(Guid projectId, Guid notebookId, IFormFileCollection files, string targetRelativePath, bool index = false, bool forceMarkdownExtraction = false) => Task.FromResult<IEnumerable<NotebookFileDto>>([]);
        public Task<NotebookFolderTreeDto?> CreateFolderAsync(Guid projectId, Guid notebookId, string newFolderPath) => Task.FromResult<NotebookFolderTreeDto?>(null);
        public Task<bool> DeleteAsync(Guid projectId, Guid notebookId, string relativePath) => Task.FromResult(false);
        public Task<bool> RenameAsync(Guid projectId, Guid notebookId, string sourceRelativePath, string newName) => Task.FromResult(false);
        public Task<bool> MoveAsync(Guid projectId, Guid notebookId, string sourceRelativePath, string destinationRelativePath) => Task.FromResult(false);
        public Task<bool> DeleteByIdAsync(Guid projectId, Guid notebookId, Guid fileId) => Task.FromResult(false);
        public Task<bool> RenameByIdAsync(Guid projectId, Guid notebookId, Guid fileId, string newName) => Task.FromResult(false);
        public Task<bool> MoveByIdAsync(Guid projectId, Guid notebookId, Guid fileId, string? destinationPath) => Task.FromResult(false);
        public Task<ContentFileDetailsDto> PublishToProjectAsync(Guid projectId, Guid notebookId, Guid notebookFileId, Guid? destinationFolderId, bool index) => throw new NotImplementedException();
        public Task<OriginFileInfoDto?> GetOriginFileInfoAsync(Guid projectId, Guid contentFileVersionId) => Task.FromResult<OriginFileInfoDto?>(null);
        public Task<NotebookFileDto> CreateTextFileAsync(Guid projectId, Guid notebookId, string relativePath, string content) => throw new NotImplementedException();
    }
}
