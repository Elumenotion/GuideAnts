using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class ProjectFolderEndpointsTests : BaseEndpointTest
{
    private Guid _projectId;
    private Project _project = null!;

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

        _project = new Project
        {
            Title = "Test Project",
            Description = "Test Description"
        };
        context.Projects.Add(_project);
        await context.SaveChangesAsync();
        _projectId = _project.Id;
    }

    #region Folder CRUD Tests

    [TestMethod]
    public async Task CreateFolder_WithValidData_ReturnsCreated()
    {
        // Arrange
        var createDto = new CreateFolderDto("TestFolder", null);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/projects/{_projectId}/folders", createDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        
        var result = await response.Content.ReadFromJsonAsync<ProjectFolderDto>();
        result.Should().NotBeNull();
        result!.Name.Should().Be("TestFolder");
        result.RelativePath.Should().Be("TestFolder");
        result.ParentFolderId.Should().BeNull();

        // Verify in database
        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var folderInDb = await context.ProjectFolders.FirstOrDefaultAsync(f => f.Id == result.Id);
        folderInDb.Should().NotBeNull();
        folderInDb!.Name.Should().Be("TestFolder");
    }

    [TestMethod]
    public async Task CreateFolder_WithParent_ReturnsCreatedWithCorrectPath()
    {
        // Arrange
        var parentFolder = new ProjectFolder
        {
            Name = "Parent",
            RelativePath = "Parent",
            ProjectId = _projectId
        };
        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ProjectFolders.Add(parentFolder);
        await context.SaveChangesAsync();

        var createDto = new CreateFolderDto("Child", parentFolder.Id);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/projects/{_projectId}/folders", createDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        
        var result = await response.Content.ReadFromJsonAsync<ProjectFolderDto>();
        result.Should().NotBeNull();
        result!.Name.Should().Be("Child");
        result.RelativePath.Should().Be("Parent/Child");
        result.ParentFolderId.Should().Be(parentFolder.Id);
    }

    [TestMethod]
    public async Task CreateFolder_WithInvalidName_ReturnsBadRequest()
    {
        // Arrange
        var createDto = new CreateFolderDto("Invalid/Name", null);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/projects/{_projectId}/folders", createDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task CreateFolder_WithDuplicatePath_ReturnsBadRequest()
    {
        // Arrange
        var existingFolder = new ProjectFolder
        {
            Name = "TestFolder",
            RelativePath = "TestFolder",
            ProjectId = _projectId
        };
        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ProjectFolders.Add(existingFolder);
        await context.SaveChangesAsync();

        var createDto = new CreateFolderDto("TestFolder", null);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/projects/{_projectId}/folders", createDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task GetFolder_ExistingFolder_ReturnsOk()
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

        // Act
        var response = await Client.GetAsync($"/api/projects/{_projectId}/folders/{folder.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var result = await response.Content.ReadFromJsonAsync<ProjectFolderDto>();
        result.Should().NotBeNull();
        result!.Id.Should().Be(folder.Id);
        result.Name.Should().Be("TestFolder");
    }

    [TestMethod]
    public async Task GetFolder_NonExistentFolder_ReturnsNotFound()
    {
        // Act
        var response = await Client.GetAsync($"/api/projects/{_projectId}/folders/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task UpdateFolder_WithNewName_ReturnsOk()
    {
        // Arrange
        var folder = new ProjectFolder
        {
            Name = "OldName",
            RelativePath = "OldName",
            ProjectId = _projectId
        };
        using var scope1 = SharedFactory!.Services.CreateScope();
        var context1 = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context1.ProjectFolders.Add(folder);
        await context1.SaveChangesAsync();

        var updateDto = new UpdateFolderDto("NewName", null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/projects/{_projectId}/folders/{folder.Id}", updateDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var result = await response.Content.ReadFromJsonAsync<ProjectFolderDto>();
        result.Should().NotBeNull();
        result!.Name.Should().Be("NewName");
        result.RelativePath.Should().Be("NewName");

        // Verify in database
        using var scope2 = SharedFactory!.Services.CreateScope();
        var context2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var updatedFolder = await context2.ProjectFolders.FirstOrDefaultAsync(f => f.Id == folder.Id);
        updatedFolder!.Name.Should().Be("NewName");
    }

    [TestMethod]
    public async Task DeleteFolder_EmptyFolder_ReturnsNoContent()
    {
        // Arrange
        var folder = new ProjectFolder
        {
            Name = "TestFolder",
            RelativePath = "TestFolder",
            ProjectId = _projectId
        };
        using var scope1 = SharedFactory!.Services.CreateScope();
        var context1 = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context1.ProjectFolders.Add(folder);
        await context1.SaveChangesAsync();

        // Act
        var response = await Client.DeleteAsync($"/api/projects/{_projectId}/folders/{folder.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify deletion in database
        using var scope2 = SharedFactory!.Services.CreateScope();
        var context2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var deletedFolder = await context2.ProjectFolders.FirstOrDefaultAsync(f => f.Id == folder.Id);
        deletedFolder.Should().BeNull();
    }

    [TestMethod]
    public async Task DeleteFolder_NonEmptyFolder_ReturnsBadRequest()
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

        var file = new ContentFile
        {
            FileName = "test.txt",
            Path = "test.txt",
            RelativePath = "TestFolder/test.txt",
            ProjectId = _projectId,
            FolderId = folder.Id,
            ContentType = "text/plain",
            FileSize = 100
        };
        file.GenerateDocumentId();
        context.ContentFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        var response = await Client.DeleteAsync($"/api/projects/{_projectId}/folders/{folder.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Folder Tree Tests

    [TestMethod]
    public async Task GetFolderTree_WithNestedStructure_ReturnsCorrectTree()
    {
        // Arrange
        var parentFolder = new ProjectFolder
        {
            Name = "Parent",
            RelativePath = "Parent",
            ProjectId = _projectId
        };
        var childFolder = new ProjectFolder
        {
            Name = "Child",
            RelativePath = "Parent/Child",
            ProjectId = _projectId,
            ParentFolderId = parentFolder.Id
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
        context.ProjectFolders.AddRange(parentFolder, childFolder);
        context.ContentFiles.Add(file);
        await context.SaveChangesAsync();

        // Act
        var response = await Client.GetAsync($"/api/projects/{_projectId}/folders/tree");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var result = await response.Content.ReadFromJsonAsync<FolderTreeDto>();
        result.Should().NotBeNull();
        result!.Name.Should().Be(_project.Title);
        result.SubFolders.Should().HaveCount(1);
        result.Files.Should().HaveCount(1);
        
        var parentInTree = result.SubFolders.First();
        parentInTree.Name.Should().Be("Parent");
        parentInTree.SubFolders.Should().HaveCount(1);
        
        var childInTree = parentInTree.SubFolders.First();
        childInTree.Name.Should().Be("Child");
    }

    [TestMethod]
    public async Task GetFolders_ReturnsAllFolders()
    {
        // Arrange
        var folder1 = new ProjectFolder
        {
            Name = "Folder1",
            RelativePath = "Folder1",
            ProjectId = _projectId
        };
        var folder2 = new ProjectFolder
        {
            Name = "Folder2",
            RelativePath = "Folder2",
            ProjectId = _projectId
        };
        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ProjectFolders.AddRange(folder1, folder2);
        await context.SaveChangesAsync();

        // Act
        var response = await Client.GetAsync($"/api/projects/{_projectId}/folders");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var result = await response.Content.ReadFromJsonAsync<List<ProjectFolderDto>>();
        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
        result.Should().Contain(f => f.Name == "Folder1");
        result.Should().Contain(f => f.Name == "Folder2");
    }

    #endregion

    #region Move Folder Tests

    [TestMethod]
    public async Task MoveFolder_ValidMove_ReturnsNoContent()
    {
        // Arrange
        var sourceFolder = new ProjectFolder
        {
            Name = "Source",
            RelativePath = "Source",
            ProjectId = _projectId
        };
        var targetFolder = new ProjectFolder
        {
            Name = "Target",
            RelativePath = "Target",
            ProjectId = _projectId
        };
        using var scope1 = SharedFactory!.Services.CreateScope();
        var context1 = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context1.ProjectFolders.AddRange(sourceFolder, targetFolder);
        await context1.SaveChangesAsync();

        var moveDto = new MoveFolderDto(sourceFolder.Id, targetFolder.Id);

        // Act
        var response = await Client.PatchAsJsonAsync($"/api/projects/{_projectId}/folders/{sourceFolder.Id}/move", moveDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify in database
        using var scope2 = SharedFactory!.Services.CreateScope();
        var context2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var movedFolder = await context2.ProjectFolders.FirstOrDefaultAsync(f => f.Id == sourceFolder.Id);
        movedFolder!.ParentFolderId.Should().Be(targetFolder.Id);
        movedFolder.RelativePath.Should().Be("Target/Source");
    }

    [TestMethod]
    public async Task MoveFolder_CircularReference_ReturnsBadRequest()
    {
        // Arrange
        var parentFolder = new ProjectFolder
        {
            Name = "Parent",
            RelativePath = "Parent",
            ProjectId = _projectId
        };
        var childFolder = new ProjectFolder
        {
            Name = "Child",
            RelativePath = "Parent/Child",
            ProjectId = _projectId,
            ParentFolderId = parentFolder.Id
        };
        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.ProjectFolders.AddRange(parentFolder, childFolder);
        await context.SaveChangesAsync();

        var moveDto = new MoveFolderDto(parentFolder.Id, childFolder.Id);

        // Act
        var response = await Client.PatchAsJsonAsync($"/api/projects/{_projectId}/folders/{parentFolder.Id}/move", moveDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

} 