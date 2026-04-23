using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class ProjectContentFileWithFoldersEndpointsTests : BaseEndpointTest
{
    private Guid _projectId;
    private Project _project = null!;
    private string _testUserId = null!;

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        SetupAuthentication();

        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Create test user
        var testUser = new User
        {
            Email = "test@example.com",
            Name = "Test User"
        };
        context.Users.Add(testUser);
        await context.SaveChangesAsync();
        _testUserId = testUser.Id.ToString();

        _project = new Project
        {
            Title = "Test Project",
            Description = "Test Description"
        };
        context.Projects.Add(_project);
        await context.SaveChangesAsync();
        _projectId = _project.Id;
    }

    #region File Upload with Folder Tests

    [TestMethod]
    public async Task UploadFile_WithFolderId_PlacesFileInFolder()
    {
        // Arrange
        var folder = new ProjectFolder
        {
            Name = "TestFolder",
            RelativePath = "TestFolder",
            ProjectId = _projectId
        };
        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ProjectFolders.Add(folder);
        await context.SaveChangesAsync();

        var content = "Test file content";
        var fileContent = new StringContent(content, Encoding.UTF8);
        using var formData = new MultipartFormDataContent();
        formData.Add(fileContent, "file", "test.txt");
        formData.Add(new StringContent(folder.Id.ToString()), "folderId");

        // Act
        var response = await Client!.PostAsync($"/api/projects/{_projectId}/files", formData);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        
        var result = await response.Content.ReadFromJsonAsync<ContentFileDetailsDto>();
        result.Should().NotBeNull();
        result!.FileName.Should().Be("test.txt");
        result.RelativePath.Should().Be("TestFolder/test.txt");
        result.FolderId.Should().Be(folder.Id);

        // Verify in database
        using var scope2 = SharedFactory!.Services.CreateScope();
        var context2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fileInDb = await context2.ContentFiles.FirstOrDefaultAsync(f => f.Id == result.Id);
        fileInDb.Should().NotBeNull();
        fileInDb!.FolderId.Should().Be(folder.Id);
        fileInDb.RelativePath.Should().Be("TestFolder/test.txt");
    }

    [TestMethod]
    public async Task UploadFile_WithoutFolderId_PlacesFileInRoot()
    {
        // Arrange
        var content = "Test file content";
        var fileContent = new StringContent(content, Encoding.UTF8);
        using var formData = new MultipartFormDataContent();
        formData.Add(fileContent, "file", "test.txt");

        // Act
        var response = await Client!.PostAsync($"/api/projects/{_projectId}/files", formData);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        
        var result = await response.Content.ReadFromJsonAsync<ContentFileDetailsDto>();
        result.Should().NotBeNull();
        result!.FileName.Should().Be("test.txt");
        result.RelativePath.Should().Be("test.txt");
        result.FolderId.Should().BeNull();
    }

    [TestMethod]
    public async Task UploadFile_WithInvalidFolderId_ReturnsBadRequest()
    {
        // Arrange
        var content = "Test file content";
        var fileContent = new StringContent(content, Encoding.UTF8);
        using var formData = new MultipartFormDataContent();
        formData.Add(fileContent, "file", "test.txt");
        formData.Add(new StringContent(Guid.NewGuid().ToString()), "folderId");

        // Act
        var response = await Client!.PostAsync($"/api/projects/{_projectId}/files", formData);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Move File Tests

    [TestMethod]
    public async Task MoveFile_ToFolder_UpdatesFileLocation()
    {
        // Arrange
        var folder = new ProjectFolder
        {
            Name = "TargetFolder",
            RelativePath = "TargetFolder",
            ProjectId = _projectId
        };
        var file = new ContentFile
        {
            FileName = "test.txt",
            Path = "test.txt",
            RelativePath = "test.txt",
            ProjectId = _projectId,
            ContentType = "text/plain",
            FileSize = 100
        };
        file.GenerateDocumentId();

        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ProjectFolders.Add(folder);
        context.ContentFiles.Add(file);
        await context.SaveChangesAsync();

        var moveDto = new MoveFileDto(file.Id, folder.Id);

        // Act
        var response = await Client!.PatchAsJsonAsync($"/api/projects/{_projectId}/files/{file.Id}/move", moveDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ContentFileDetailsDto>();
        result.Should().NotBeNull();
        result!.FolderId.Should().Be(folder.Id);
        result.RelativePath.Should().Be("TargetFolder/test.txt");

        // Verify in database
        using var scope2 = SharedFactory!.Services.CreateScope();
        var context2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var movedFile = await context2.ContentFiles.FirstOrDefaultAsync(f => f.Id == file.Id);
        movedFile.Should().NotBeNull();
        movedFile!.FolderId.Should().Be(folder.Id);
        movedFile.RelativePath.Should().Be("TargetFolder/test.txt");
    }

    [TestMethod]
    public async Task MoveFile_ToRoot_UpdatesFileLocation()
    {
        // Arrange
        var folder = new ProjectFolder
        {
            Name = "SourceFolder",
            RelativePath = "SourceFolder",
            ProjectId = _projectId
        };
        var file = new ContentFile
        {
            FileName = "test.txt",
            Path = "test.txt",
            RelativePath = "SourceFolder/test.txt",
            ProjectId = _projectId,
            FolderId = folder.Id,
            ContentType = "text/plain",
            FileSize = 100
        };
        file.GenerateDocumentId();

        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ProjectFolders.Add(folder);
        context.ContentFiles.Add(file);
        await context.SaveChangesAsync();

        var moveDto = new MoveFileDto(file.Id, null);

        // Act
        var response = await Client!.PatchAsJsonAsync($"/api/projects/{_projectId}/files/{file.Id}/move", moveDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ContentFileDetailsDto>();
        result.Should().NotBeNull();
        result!.FolderId.Should().BeNull();
        result.RelativePath.Should().Be("test.txt");

        // Verify in database
        using var scope2 = SharedFactory!.Services.CreateScope();
        var context2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var movedFile = await context2.ContentFiles.FirstOrDefaultAsync(f => f.Id == file.Id);
        movedFile.Should().NotBeNull();
        movedFile!.FolderId.Should().BeNull();
        movedFile.RelativePath.Should().Be("test.txt");
    }

    [TestMethod]
    public async Task MoveFile_ToInvalidFolder_ReturnsBadRequest()
    {
        // Arrange
        var file = new ContentFile
        {
            FileName = "test.txt",
            Path = "test.txt",
            RelativePath = "test.txt",
            ProjectId = _projectId,
            ContentType = "text/plain",
            FileSize = 100
        };
        file.GenerateDocumentId();

        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ContentFiles.Add(file);
        await context.SaveChangesAsync();

        var moveDto = new MoveFileDto(file.Id, Guid.NewGuid());

        // Act
        var response = await Client!.PatchAsJsonAsync($"/api/projects/{_projectId}/files/{file.Id}/move", moveDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task MoveFile_NonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var moveDto = new MoveFileDto(Guid.NewGuid(), null);

        // Act
        var response = await Client!.PatchAsJsonAsync($"/api/projects/{_projectId}/files/{Guid.NewGuid()}/move", moveDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task MoveFile_DuplicateNameInFolder_ReturnsBadRequest()
    {
        // Arrange
        var folder = new ProjectFolder
        {
            Name = "TestFolder",
            RelativePath = "TestFolder",
            ProjectId = _projectId
        };
        var existingFile = new ContentFile
        {
            FileName = "duplicate.txt",
            Path = "duplicate.txt",
            RelativePath = "TestFolder/duplicate.txt",
            ProjectId = _projectId,
            FolderId = folder.Id,
            ContentType = "text/plain",
            FileSize = 100
        };
        existingFile.GenerateDocumentId();
        
        var fileToMove = new ContentFile
        {
            FileName = "duplicate.txt",
            Path = "duplicate.txt",
            RelativePath = "duplicate.txt",
            ProjectId = _projectId,
            ContentType = "text/plain",
            FileSize = 100
        };
        fileToMove.GenerateDocumentId();

        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ProjectFolders.Add(folder);
        context.ContentFiles.AddRange(existingFile, fileToMove);
        await context.SaveChangesAsync();

        var moveDto = new MoveFileDto(fileToMove.Id, folder.Id);

        // Act
        var response = await Client!.PatchAsJsonAsync($"/api/projects/{_projectId}/files/{fileToMove.Id}/move", moveDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Authorization Tests


    #endregion
} 