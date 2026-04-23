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
public class FileLineageEndpointsTests : BaseEndpointTest
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
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Ensure test user exists and owns a project
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == "test@example.com");
        if (user == null)
        {
            user = new User { Email = "test@example.com", Name = "Test User" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var project = new Project
        {
            Title = "Lineage Test Project",
            Description = "Integration"
        };
        db.Projects.Add(project);

        await db.SaveChangesAsync();
        _projectId = project.Id;
    }

    private async Task<(ContentFileDetailsDto File, FileLineageEvent Lineage)> UploadFileAndGetLineageAsync(string name, string content)
    {
        // Upload a file via API
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(content, Encoding.UTF8), "file", name);
        var response = await Client!.PostAsync($"/api/projects/{_projectId}/files", form);
        response.EnsureSuccessStatusCode();
        var details = (await response.Content.ReadFromJsonAsync<ContentFileDetailsDto>())!;

        // Fetch lineage event from DB
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ev = await db.FileLineageEvents.FirstAsync(e => e.FileId == details.Id && e.Action == FileLineageAction.Uploaded);
        return (details, ev);
    }

    [TestMethod]
    public async Task GetLineageEvent_ReturnsDto()
    {
        var (file, ev) = await UploadFileAndGetLineageAsync("lineage.txt", "hello lineage");

        var resp = await Client!.GetAsync($"/api/lineage/{ev.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        doc!.RootElement.GetProperty("id").GetGuid().Should().Be(ev.Id);
        doc.RootElement.GetProperty("fileId").GetGuid().Should().Be(file.Id);
        doc.RootElement.GetProperty("action").GetString().Should().Be("Uploaded");
    }

    [TestMethod]
    public async Task DownloadLineageEvent_ReturnsFileContent()
    {
        var (_, ev) = await UploadFileAndGetLineageAsync("lineage-download.txt", "download me");
        var resp = await Client!.GetAsync($"/api/lineage/{ev.Id}/download");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await resp.Content.ReadAsStringAsync();
        text.Should().Be("download me");
    }

    [TestMethod]
    public async Task GetFileHistory_ReturnsEvents()
    {
        var (file, _) = await UploadFileAndGetLineageAsync("history.txt", "abc");
        var resp = await Client!.GetAsync($"/api/projects/{_projectId}/files/{file.Id}/history");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        json.GetArrayLength().Should().BeGreaterThan(0);
        var first = json[0];
        first.GetProperty("fileId").GetGuid().Should().Be(file.Id);
        first.GetProperty("action").GetString().Should().Be("Uploaded");
    }
} 