using FluentAssertions;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class DocumentServerServiceTests
{
    [TestMethod]
    public async Task BuildEditorConfigAsync_WhenEnabled_ReturnsConfigPayload()
    {
        await using var db = CreateDbContext();
        var service = CreateService(
            db,
            new StubContentFileService(
                details: new ContentFileDetailsDto(
                    Id: Guid.Parse("64af2fec-1306-4d8b-bf97-41ab6f83d184"),
                    FileName: "proposal.docx",
                    Path: string.Empty,
                    RelativePath: "proposal.docx",
                    ContentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    Index: false,
                    DocumentId: "doc-1",
                    Created: DateTime.UtcNow,
                    FileSize: 42,
                    FolderId: null,
                    FolderPath: null,
                    LatestVersion: 3,
                    IsSnapshot: false,
                    HasMarkdownShadow: false,
                    MarkdownStatus: null,
                    MarkdownProcessedAt: null)),
            enabled: true);

        var httpContext = CreateHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost:5107");

        var result = await service.BuildEditorConfigAsync(
            httpContext,
            new DocumentServerEditorConfigRequest(
                Scope: "project",
                ProjectId: Guid.NewGuid(),
                FileId: Guid.Parse("64af2fec-1306-4d8b-bf97-41ab6f83d184"),
                NotebookId: null,
                CanEdit: true,
                UserId: "user-1",
                UserName: "Test User"),
            CancellationToken.None);

        result.DocumentServerUrl.Should().Be("http://localhost:5107/api/documentserver/ds");
        result.Config.Should().NotBeNull();
        var config = (Dictionary<string, object?>)result.Config;
        var editorConfig = (Dictionary<string, object?>)config["editorConfig"]!;
        var customization = (Dictionary<string, object?>)editorConfig["customization"]!;
        customization["forcesave"].Should().Be(true);
        customization["autosave"].Should().Be(true);
    }

    [TestMethod]
    public async Task BuildEditorConfigAsync_WhenDisabled_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(
            db,
            new StubContentFileService(
                details: new ContentFileDetailsDto(
                    Id: Guid.NewGuid(),
                    FileName: "proposal.docx",
                    Path: string.Empty,
                    RelativePath: "proposal.docx",
                    ContentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    Index: false,
                    DocumentId: "doc-1",
                    Created: DateTime.UtcNow,
                    FileSize: 42,
                    FolderId: null,
                    FolderPath: null,
                    LatestVersion: 1,
                    IsSnapshot: false,
                    HasMarkdownShadow: false,
                    MarkdownStatus: null,
                    MarkdownProcessedAt: null)),
            enabled: false);

        var httpContext = CreateHttpContext();

        var action = async () => await service.BuildEditorConfigAsync(
            httpContext,
            new DocumentServerEditorConfigRequest("project", Guid.NewGuid(), Guid.NewGuid(), null, false, null, null),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task GetDownloadAsync_InvalidToken_Throws()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db, new StubContentFileService(details: null), enabled: true);

        var action = async () => await service.GetDownloadAsync("invalid-token", null, null, null, null, null, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*token payload is invalid*");
    }

    [TestMethod]
    public async Task GetDownloadAsync_WithValidToken_ReturnsProjectFile()
    {
        await using var db = CreateDbContext();
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var expectedBytes = Encoding.UTF8.GetBytes("download-bytes");
        var contentService = new StubContentFileService(
            details: new ContentFileDetailsDto(
                Id: fileId,
                FileName: "proposal.docx",
                Path: string.Empty,
                RelativePath: "proposal.docx",
                ContentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                Index: false,
                DocumentId: "doc-1",
                Created: DateTime.UtcNow,
                FileSize: expectedBytes.Length,
                FolderId: null,
                FolderPath: null,
                LatestVersion: 2,
                IsSnapshot: false,
                HasMarkdownShadow: false,
                MarkdownStatus: null,
                MarkdownProcessedAt: null),
            versionContent: new ContentFileContentDto
            {
                Content = expectedBytes,
                ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                FileName = "proposal.docx"
            });

        var service = CreateService(db, contentService, enabled: true);
        var httpContext = CreateHttpContext();

        var tokenResult = await service.BuildEditorConfigAsync(
            httpContext,
            new DocumentServerEditorConfigRequest(
                Scope: "project",
                ProjectId: projectId,
                FileId: fileId,
                NotebookId: null,
                CanEdit: true,
                UserId: "user-1",
                UserName: "Test User"),
            CancellationToken.None);

        var config = (Dictionary<string, object?>)tokenResult.Config;
        var document = (Dictionary<string, object?>)config["document"]!;
        var downloadUrl = document["url"]!.ToString()!;
        var token = ExtractToken(downloadUrl);

        var result = await service.GetDownloadAsync(token, null, null, null, null, null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.FileName.Should().Be("proposal.docx");
        result.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        contentService.LastRequestedVersionNumber.Should().Be(2);
        await using var stream = result.Stream;
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        memory.ToArray().Should().Equal(expectedBytes);
    }

    [TestMethod]
    public async Task HandleCallbackAsync_NonSaveStatus_DoesNotThrow()
    {
        await using var db = CreateDbContext();
        var fileId = Guid.NewGuid();
        var service = CreateService(
            db,
            new StubContentFileService(
                details: new ContentFileDetailsDto(
                    Id: fileId,
                    FileName: "proposal.docx",
                    Path: string.Empty,
                    RelativePath: "proposal.docx",
                    ContentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    Index: false,
                    DocumentId: "doc-1",
                    Created: DateTime.UtcNow,
                    FileSize: 42,
                    FolderId: null,
                    FolderPath: null,
                    LatestVersion: 1,
                    IsSnapshot: false,
                    HasMarkdownShadow: false,
                    MarkdownStatus: null,
                    MarkdownProcessedAt: null)),
            enabled: true);

        var httpContext = CreateHttpContext();

        var tokenResult = await service.BuildEditorConfigAsync(
            httpContext,
            new DocumentServerEditorConfigRequest(
                Scope: "project",
                ProjectId: Guid.NewGuid(),
                FileId: fileId,
                NotebookId: null,
                CanEdit: false,
                UserId: null,
                UserName: null),
            CancellationToken.None);

        var config = (Dictionary<string, object?>)tokenResult.Config;
        var editorConfig = (Dictionary<string, object?>)config["editorConfig"]!;
        var callbackUrl = editorConfig["callbackUrl"]!.ToString()!;
        var token = callbackUrl.Split("token=", StringSplitOptions.RemoveEmptyEntries).Last();

        var action = async () => await service.HandleCallbackAsync(
            token,
            null,
            null,
            null,
            null,
            new DocumentServerCallbackPayload(Status: 1, Url: null),
            CancellationToken.None);

        await action.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task HandleCallbackAsync_SaveStatus_UploadsUpdatedProjectVersion()
    {
        await using var db = CreateDbContext();
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var contentService = new StubContentFileService(
            details: new ContentFileDetailsDto(
                Id: fileId,
                FileName: "proposal.docx",
                Path: string.Empty,
                RelativePath: "proposal.docx",
                ContentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                Index: false,
                DocumentId: "doc-1",
                Created: DateTime.UtcNow,
                FileSize: 42,
                FolderId: null,
                FolderPath: null,
                LatestVersion: 1,
                IsSnapshot: false,
                HasMarkdownShadow: false,
                MarkdownStatus: null,
                MarkdownProcessedAt: null));
        var callbackBytes = Encoding.UTF8.GetBytes("updated-content");
        var service = CreateService(
            db,
            contentService,
            enabled: true,
            httpClientFactory: new StubHttpClientFactory(callbackBytes));

        var tokenResult = await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest(
                Scope: "project",
                ProjectId: projectId,
                FileId: fileId,
                NotebookId: null,
                CanEdit: true,
                UserId: "user-1",
                UserName: "Test User"),
            CancellationToken.None);

        var config = (Dictionary<string, object?>)tokenResult.Config;
        var editorConfig = (Dictionary<string, object?>)config["editorConfig"]!;
        var callbackUrl = editorConfig["callbackUrl"]!.ToString()!;
        var token = ExtractToken(callbackUrl);

        await service.HandleCallbackAsync(
            token,
            null,
            null,
            null,
            null,
            new DocumentServerCallbackPayload(Status: 2, Url: "http://callback.local/file.docx"),
            CancellationToken.None);

        contentService.UploadCalls.Should().Be(1);
        contentService.LastUploadedFileName.Should().Be("proposal.docx");
        contentService.LastUploadedBytes.Should().Equal(callbackBytes);
    }

    [TestMethod]
    public async Task HandleCallbackAsync_SaveStatus_RewritesPublicDocumentServerUrlToInternalUrl()
    {
        await using var db = CreateDbContext();
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var contentService = new StubContentFileService(
            details: new ContentFileDetailsDto(
                Id: fileId,
                FileName: "proposal.docx",
                Path: string.Empty,
                RelativePath: "proposal.docx",
                ContentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                Index: false,
                DocumentId: "doc-1",
                Created: DateTime.UtcNow,
                FileSize: 42,
                FolderId: null,
                FolderPath: null,
                LatestVersion: 1,
                IsSnapshot: false,
                HasMarkdownShadow: false,
                MarkdownStatus: null,
                MarkdownProcessedAt: null));
        var httpClientFactory = new StubHttpClientFactory(Encoding.UTF8.GetBytes("updated-content"));
        var service = CreateService(
            db,
            contentService,
            enabled: true,
            httpClientFactory: httpClientFactory,
            internalUrl: "http://documentserver");

        var tokenResult = await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest(
                Scope: "project",
                ProjectId: projectId,
                FileId: fileId,
                NotebookId: null,
                CanEdit: true,
                UserId: "user-1",
                UserName: "Test User"),
            CancellationToken.None);

        var config = (Dictionary<string, object?>)tokenResult.Config;
        var editorConfig = (Dictionary<string, object?>)config["editorConfig"]!;
        var callbackUrl = editorConfig["callbackUrl"]!.ToString()!;
        var token = ExtractToken(callbackUrl);

        await service.HandleCallbackAsync(
            token,
            null,
            null,
            null,
            null,
            new DocumentServerCallbackPayload(Status: 2, Url: "http://localhost:5107/api/documentserver/ds/cache/files/edited.docx?token=abc"),
            CancellationToken.None);

        httpClientFactory.LastRequestUri.Should().Be(new Uri("http://documentserver/cache/files/edited.docx?token=abc"));
        contentService.UploadCalls.Should().Be(1);
    }

    [TestMethod]
    public async Task HandleCallbackAsync_SaveStatus_WithInvalidCallbackUrl_Throws()
    {
        await using var db = CreateDbContext();
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var service = CreateService(
            db,
            new StubContentFileService(
                details: new ContentFileDetailsDto(
                    Id: fileId,
                    FileName: "proposal.docx",
                    Path: string.Empty,
                    RelativePath: "proposal.docx",
                    ContentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    Index: false,
                    DocumentId: "doc-1",
                    Created: DateTime.UtcNow,
                    FileSize: 42,
                    FolderId: null,
                    FolderPath: null,
                    LatestVersion: 1,
                    IsSnapshot: false,
                    HasMarkdownShadow: false,
                    MarkdownStatus: null,
                    MarkdownProcessedAt: null)),
            enabled: true);

        var tokenResult = await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest(
                Scope: "project",
                ProjectId: projectId,
                FileId: fileId,
                NotebookId: null,
                CanEdit: true,
                UserId: "user-1",
                UserName: "Test User"),
            CancellationToken.None);

        var config = (Dictionary<string, object?>)tokenResult.Config;
        var editorConfig = (Dictionary<string, object?>)config["editorConfig"]!;
        var callbackUrl = editorConfig["callbackUrl"]!.ToString()!;
        var token = ExtractToken(callbackUrl);

        var action = async () => await service.HandleCallbackAsync(
            token,
            null,
            null,
            null,
            null,
            new DocumentServerCallbackPayload(Status: 2, Url: "cache/files/edited.docx?token=abc"),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be an absolute URL*");
    }

    [TestMethod]
    public async Task HandleCallbackAsync_ForceSaveStatus_UploadsUpdatedNotebookFile()
    {
        await using var db = CreateDbContext();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        db.NotebookFiles.Add(new GuideAntsApi.DataModel.Models.NotebookFile
        {
            Id = fileId,
            NotebookId = notebookId,
            RelativePath = "docs/dsa.docx",
            FileSize = 10,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "old-hash"
        });
        await db.SaveChangesAsync();

        var callbackBytes = Encoding.UTF8.GetBytes("updated-notebook-content");
        var notebookFileService = new StubNotebookFileService();
        var service = CreateService(
            db,
            new StubContentFileService(details: null),
            enabled: true,
            httpClientFactory: new StubHttpClientFactory(callbackBytes),
            notebookFileService: notebookFileService);

        var tokenResult = await service.BuildEditorConfigAsync(
            CreateHttpContext(),
            new DocumentServerEditorConfigRequest(
                Scope: "notebook",
                ProjectId: projectId,
                FileId: fileId,
                NotebookId: notebookId,
                CanEdit: true,
                UserId: "user-1",
                UserName: "Test User"),
            CancellationToken.None);

        var config = (Dictionary<string, object?>)tokenResult.Config;
        var editorConfig = (Dictionary<string, object?>)config["editorConfig"]!;
        var callbackUrl = editorConfig["callbackUrl"]!.ToString()!;
        var token = ExtractToken(callbackUrl);

        await service.HandleCallbackAsync(
            token,
            null,
            null,
            null,
            null,
            new DocumentServerCallbackPayload(Status: 6, Url: "http://callback.local/file.docx"),
            CancellationToken.None);

        notebookFileService.UploadCalls.Should().Be(1);
        notebookFileService.LastProjectId.Should().Be(projectId);
        notebookFileService.LastNotebookId.Should().Be(notebookId);
        notebookFileService.LastTargetRelativePath.Should().Be("docs");
        notebookFileService.LastUploadedFileName.Should().Be("dsa.docx");
        notebookFileService.LastUploadedBytes.Should().Equal(callbackBytes);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"documentserver-tests-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }

    private static DocumentServerService CreateService(
        ApplicationDbContext db,
        IContentFileService contentFileService,
        bool enabled,
        IHttpClientFactory? httpClientFactory = null,
        INotebookFileService? notebookFileService = null,
        string internalUrl = "http://documentserver")
    {
        return new DocumentServerService(
            db,
            contentFileService,
            notebookFileService ?? new StubNotebookFileService(),
            Microsoft.Extensions.Options.Options.Create(new DocumentServerOptions
            {
                Enabled = enabled,
                InternalUrl = internalUrl,
                ApiBaseUrl = "http://host.docker.internal:5106",
                JwtEnabled = true,
                JwtSecret = "documentserver-tests-secret"
            }),
            httpClientFactory ?? new StubHttpClientFactory(),
            NullLogger<DocumentServerService>.Instance);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly byte[]? _responseBytes;
        public Uri? LastRequestUri { get; private set; }

        public StubHttpClientFactory(byte[]? responseBytes = null)
        {
            _responseBytes = responseBytes;
        }

        public HttpClient CreateClient(string name)
        {
            if (_responseBytes == null)
            {
                return new HttpClient();
            }

            return new HttpClient(new FixedResponseMessageHandler(_responseBytes, requestUri => LastRequestUri = requestUri));
        }
    }

    private sealed class FixedResponseMessageHandler : HttpMessageHandler
    {
        private readonly byte[] _responseBytes;
        private readonly Action<Uri?> _captureRequestUri;

        public FixedResponseMessageHandler(byte[] responseBytes, Action<Uri?> captureRequestUri)
        {
            _responseBytes = responseBytes;
            _captureRequestUri = captureRequestUri;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _captureRequestUri(request.RequestUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_responseBytes)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StubContentFileService : IContentFileService
    {
        private readonly ContentFileDetailsDto? _details;
        private readonly ContentFileContentDto? _versionContent;

        public int UploadCalls { get; private set; }
        public byte[] LastUploadedBytes { get; private set; } = [];
        public string? LastUploadedFileName { get; private set; }
        public int? LastRequestedVersionNumber { get; private set; }

        public StubContentFileService(ContentFileDetailsDto? details, ContentFileContentDto? versionContent = null)
        {
            _details = details;
            _versionContent = versionContent;
        }

        public async Task<ContentFileDetailsDto> UploadFileAsync(Guid projectId, IFormFile file, bool index = false, Guid? folderId = null)
        {
            UploadCalls++;
            LastUploadedFileName = file.FileName;
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            LastUploadedBytes = memory.ToArray();

            return _details ?? throw new InvalidOperationException("No content file configured.");
        }

        public Task<ContentFileDetailsDto?> MoveFileAsync(Guid projectId, Guid fileId, Guid? destinationFolderId) => Task.FromResult<ContentFileDetailsDto?>(null);
        public Task<bool> DeleteAsync(Guid projectId, Guid fileId) => Task.FromResult(false);
        public Task<ContentFileDetailsDto?> GetAsync(Guid projectId, Guid fileId) => Task.FromResult(_details);
        public Task<IEnumerable<ContentFileDetailsDto>> GetAllForProjectAsync(Guid projectId) => Task.FromResult<IEnumerable<ContentFileDetailsDto>>([]);
        public Task<ContentFileDetailsDto?> UpdateAsync(Guid projectId, Guid fileId, UpdateContentFileDto updates) => Task.FromResult<ContentFileDetailsDto?>(null);
        public Task<ContentFileContentDto?> GetContentAsync(Guid projectId, Guid fileId) => Task.FromResult<ContentFileContentDto?>(null);
        public Task<IEnumerable<ContentFileVersionDto>> GetVersionsAsync(Guid projectId, Guid fileId) => Task.FromResult<IEnumerable<ContentFileVersionDto>>([]);
        public Task<ContentFileContentDto?> GetVersionContentAsync(Guid projectId, Guid fileId, int versionNumber)
        {
            LastRequestedVersionNumber = versionNumber;
            return Task.FromResult(_versionContent);
        }
        public Task<ContentFileDetailsDto> CreateFileFromPathAsync(Guid projectId, string sourcePath, string fileName, string contentType, Guid? folderId, Guid originNotebookFileId, bool index = false) => Task.FromResult(_details!);
        public Task<ContentFileDetailsDto> CreateVersionFromPathAsync(Guid projectId, Guid contentFileId, string sourcePath, Guid originNotebookFileId, bool index = false) => Task.FromResult(_details!);
    }

    private sealed class StubNotebookFileService : INotebookFileService
    {
        public int UploadCalls { get; private set; }
        public Guid? LastProjectId { get; private set; }
        public Guid? LastNotebookId { get; private set; }
        public string? LastTargetRelativePath { get; private set; }
        public string? LastUploadedFileName { get; private set; }
        public byte[] LastUploadedBytes { get; private set; } = [];

        public Task<IEnumerable<NotebookFileDto>> ListFilesAsync(Guid projectId, Guid notebookId) => Task.FromResult<IEnumerable<NotebookFileDto>>([]);
        public Task<NotebookFolderTreeDto?> GetFolderTreeAsync(Guid projectId, Guid notebookId) => Task.FromResult<NotebookFolderTreeDto?>(null);
        public Task<(Stream Stream, string ContentType, string FileName)?> GetFileAsync(Guid projectId, Guid notebookId, string relativePath) => Task.FromResult< (Stream, string, string)?>(null);
        public Task<(Stream stream, string contentType)> GetFileContentStreamAsync(Guid projectId, Guid notebookId, string relativePath) => throw new NotImplementedException();
        public Task<(Stream Stream, string ContentType, string FileName)?> GetFileContentStreamAsync(Guid notebookFileId, CancellationToken cancellationToken = default) => Task.FromResult<(Stream, string, string)?>(null);
        public Task<NotebookFileDto?> CopyFromProjectAsync(Guid projectId, Guid notebookId, Guid contentFileId, int? versionNumber, string? targetRelativePath) => Task.FromResult<NotebookFileDto?>(null);
        public async Task<IEnumerable<NotebookFileDto>> UploadFilesAsync(Guid projectId, Guid notebookId, IFormFileCollection files, string targetRelativePath, bool index = false, bool forceMarkdownExtraction = false)
        {
            UploadCalls++;
            LastProjectId = projectId;
            LastNotebookId = notebookId;
            LastTargetRelativePath = targetRelativePath;
            var file = files.Single();
            LastUploadedFileName = file.FileName;
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            LastUploadedBytes = memory.ToArray();
            return [];
        }
        public Task<NotebookFolderTreeDto?> CreateFolderAsync(Guid projectId, Guid notebookId, string newFolderPath) => Task.FromResult<NotebookFolderTreeDto?>(null);
        public Task<bool> DeleteAsync(Guid projectId, Guid notebookId, string relativePath) => Task.FromResult(false);
        public Task<bool> RenameAsync(Guid projectId, Guid notebookId, string sourceRelativePath, string newName) => Task.FromResult(false);
        public Task<bool> MoveAsync(Guid projectId, Guid notebookId, string sourceRelativePath, string destinationRelativePath) => Task.FromResult(false);
        public Task<bool> DeleteByIdAsync(Guid projectId, Guid notebookId, Guid fileId) => Task.FromResult(false);
        public Task<bool> RenameByIdAsync(Guid projectId, Guid notebookId, Guid fileId, string newName) => Task.FromResult(false);
        public Task<bool> MoveByIdAsync(Guid projectId, Guid notebookId, Guid fileId, string? destinationPath) => Task.FromResult(false);
        public Task<ContentFileDetailsDto> PublishToProjectAsync(Guid projectId, Guid notebookId, Guid notebookFileId, Guid? destinationFolderId, bool index) => throw new NotImplementedException();
        public Task<GuideAntsApi.Endpoints.OriginFileInfoDto?> GetOriginFileInfoAsync(Guid projectId, Guid contentFileVersionId) => Task.FromResult<GuideAntsApi.Endpoints.OriginFileInfoDto?>(null);
        public Task<NotebookFileDto> CreateTextFileAsync(Guid projectId, Guid notebookId, string relativePath, string content) => throw new NotImplementedException();
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

    private static string ExtractToken(string url)
    {
        var token = url.Split("token=", StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"Token was not found in URL: {url}");
        }

        return Uri.UnescapeDataString(token);
    }
}
