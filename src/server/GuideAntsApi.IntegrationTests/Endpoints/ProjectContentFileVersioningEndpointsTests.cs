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
public class ProjectContentFileVersioningEndpointsTests : BaseEndpointTest
{
    private Guid _projectId;

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

        var testUser = await context.Users.FirstOrDefaultAsync(u => u.Email == "test@example.com");
        if (testUser == null)
        {
            testUser = new User { Email = "test@example.com", Name = "Test User" };
            context.Users.Add(testUser);
            await context.SaveChangesAsync();
        }
        
        var project = new Project
        {
            Title = "Versioning Test Project",
            Description = "Test"
        };
        context.Projects.Add(project);

        await context.SaveChangesAsync();
        _projectId = project.Id;
    }
    
    private async Task<ContentFileDetailsDto> UploadTestFile(string fileName, string content)
    {
        var fileContent = new StringContent(content, Encoding.UTF8);
        using var formData = new MultipartFormDataContent();
        formData.Add(fileContent, "file", fileName);

        var response = await Client!.PostAsync($"/api/projects/{_projectId}/files", formData);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ContentFileDetailsDto>())!;
    }

    [TestMethod]
    public async Task UploadFile_FirstVersion_CreatesVersion1()
    {
        // Act
        var result = await UploadTestFile("test.txt", "version 1");

        // Assert
        result.Should().NotBeNull();
        result.FileName.Should().Be("test.txt");
        result.LatestVersion.Should().Be(1);

        // Verify in database
        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var fileInDb = await context.ContentFiles.Include(f => f.Versions).FirstOrDefaultAsync(f => f.Id == result.Id);
        fileInDb.Should().NotBeNull();
        fileInDb!.LatestVersion.Should().Be(1);
        fileInDb.Versions.Should().HaveCount(1);
        fileInDb.Versions.First().VersionNumber.Should().Be(1);
    }

    [TestMethod]
    public async Task UploadFile_NewVersion_IncrementsVersion()
    {
        // Arrange
        await UploadTestFile("test.txt", "version 1");
        
        // Act
        var result = await UploadTestFile("test.txt", "version 2");
        
        // Assert
        result.LatestVersion.Should().Be(2);
        
        // Verify in database
        using var scope = SharedFactory!.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var fileInDb = await context.ContentFiles.Include(f => f.Versions).FirstOrDefaultAsync(f => f.Id == result.Id);
        fileInDb.Should().NotBeNull();
        fileInDb!.LatestVersion.Should().Be(2);
        fileInDb.Versions.Should().HaveCount(2);
    }
    
    [TestMethod]
    public async Task GetVersions_ReturnsAllVersions()
    {
        // Arrange
        var uploadedFile = await UploadTestFile("test.txt", "version 1");
        await UploadTestFile("test.txt", "version 2");
        
        // Act
        var response = await Client!.GetAsync($"/api/projects/{_projectId}/files/{uploadedFile.Id}/versions");
        
        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await response.Content.ReadFromJsonAsync<List<ContentFileVersionDto>>();
        versions.Should().NotBeNull();
        versions.Should().HaveCount(2);
        versions.Should().Contain(v => v.VersionNumber == 1);
        versions.Should().Contain(v => v.VersionNumber == 2);
    }
    
    [TestMethod]
    public async Task GetVersionContent_ReturnsCorrectContent()
    {
        // Arrange
        var uploadedFile = await UploadTestFile("test.txt", "version 1 content");
        await UploadTestFile("test.txt", "version 2 content");
        
        // Act & Assert
        var v1Response = await Client!.GetStringAsync($"/api/projects/{_projectId}/files/{uploadedFile.Id}/versions/1/content");
        v1Response.Should().Be("version 1 content");
        
        var v2Response = await Client!.GetStringAsync($"/api/projects/{_projectId}/files/{uploadedFile.Id}/versions/2/content");
        v2Response.Should().Be("version 2 content");
        
        var latestResponse = await Client!.GetStringAsync($"/api/projects/{_projectId}/files/{uploadedFile.Id}/content");
        latestResponse.Should().Be("version 2 content");
    }
} 