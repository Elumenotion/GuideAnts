using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.DataModel;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class NotebookFileSyncEndpointsTests
{
    private static WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext ctx)
    {
        var baseFactory = new TestWebApplicationFactory();
        await baseFactory.InitializeAsync();
        _factory = baseFactory;
    }

    [TestInitialize]
    public void TestInit()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            IntegrationTestAuthTokenFactory.CreateAdminToken());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
    }

    private async Task<Guid> GetCreativeGuideIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<GuideAntsApi.DataModel.ApplicationDbContext>(scope.ServiceProvider);
        var guideId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            System.Linq.Queryable.Select(
                System.Linq.Queryable.Where(
                    db.Assistants,
                    a => a.Kind == GuideAntsApi.DataModel.Models.AssistantKind.Guide
                        && a.IsActive
                        && a.Name == "Creative Guide"),
                a => a.Id));
        
        if (guideId == Guid.Empty)
        {
            var guide = new GuideAntsApi.DataModel.Models.Assistant { Id = Guid.NewGuid(), Name = "Test Guide", Kind = GuideAntsApi.DataModel.Models.AssistantKind.Guide, IsActive = true, IsGlobal = true };
            db.Assistants.Add(guide);
            await db.SaveChangesAsync();
            guideId = guide.Id;
        }
        return guideId;
    }

    [TestMethod]
    public async Task Sync_adds_updates_and_deletes_files()
    {
        // Arrange: create project and notebook
        var projResp = await _client.PostAsJsonAsync("/api/projects", new { title = $"p-sync-{Guid.NewGuid():N}", description = "d" });
        projResp.EnsureSuccessStatusCode();
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var nbResp = await _client.PostAsJsonAsync($"/api/projects/{proj!.Id}/notebooks", new { title = $"nb-{Guid.NewGuid():N}", guideId = await GetCreativeGuideIdAsync() });
        nbResp.EnsureSuccessStatusCode();
        var notebook = await nbResp.Content.ReadFromJsonAsync<NotebookDto>();

        var nbRoot = await GetNotebookRootPathAsync(proj!.Id, notebook!.Id);
        Directory.CreateDirectory(nbRoot);

        var filePath = Path.Combine(nbRoot, "greet.txt");

        // 1) Create file on disk
        await File.WriteAllTextAsync(filePath, "hello");

        // Act: sync
        var syncResp1 = await _client.PostAsync($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files/sync", content: null);
        syncResp1.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

        // Assert: one file recorded
        using (var scope = _factory.Services.CreateScope())
        {
            var syncService = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Components.INotebookFileSyncService>();
            await syncService.SyncNotebookAsync(notebook.Id);
        }
        var list1 = await _client.GetFromJsonAsync<List<NotebookFileDto>>($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files");
        
        list1.Should().NotBeNull();
        list1!.Should().HaveCount(1);
        list1[0].RelativePath.Should().Be("greet.txt");
        var initialHash = list1[0].FileHash;
        initialHash.Should().NotBeNullOrWhiteSpace();

        // 2) Update file content
        await File.WriteAllTextAsync(filePath, "hello world bigger");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(1));

        var syncResp2 = await _client.PostAsync($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files/sync", content: null);
        syncResp2.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

        using (var scope = _factory.Services.CreateScope())
        {
            var syncService = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Components.INotebookFileSyncService>();
            await syncService.SyncNotebookAsync(notebook.Id);
        }
        var list2 = await _client.GetFromJsonAsync<List<NotebookFileDto>>($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files");

        list2.Should().NotBeNull();
        list2!.Should().HaveCount(1);
        list2[0].FileHash.Should().NotBe(initialHash);

        // 3) Delete file
        File.Delete(filePath);

        var syncResp3 = await _client.PostAsync($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files/sync", content: null);
        syncResp3.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

        using (var scope = _factory.Services.CreateScope())
        {
            var syncService = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Components.INotebookFileSyncService>();
            await syncService.SyncNotebookAsync(notebook.Id);
        }
        var list3 = await _client.GetFromJsonAsync<List<NotebookFileDto>>($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files");

        list3.Should().NotBeNull();
        list3!.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Sync_concurrent_runs_do_not_duplicate()
    {
        // Arrange
        var projResp = await _client.PostAsJsonAsync("/api/projects", new { title = $"p-sync-{Guid.NewGuid():N}", description = "d" });
        projResp.EnsureSuccessStatusCode();
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var nbResp = await _client.PostAsJsonAsync($"/api/projects/{proj!.Id}/notebooks", new { title = $"nb-{Guid.NewGuid():N}", guideId = await GetCreativeGuideIdAsync() });
        nbResp.EnsureSuccessStatusCode();
        var notebook = await nbResp.Content.ReadFromJsonAsync<NotebookDto>();

        var nbRoot = await GetNotebookRootPathAsync(proj!.Id, notebook!.Id);
        Directory.CreateDirectory(nbRoot);

        var filePath = Path.Combine(nbRoot, "concurrent.txt");
        await File.WriteAllTextAsync(filePath, "concurrent");

        // Act: fire multiple syncs concurrently
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => _client.PostAsync($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files/sync", content: null));
        var responses = await Task.WhenAll(tasks);
        responses.Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.NoContent);

        // Manually trigger concurrent syncs to avoid background job timing issues
        var syncTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () => 
        {
            using var scope = _factory.Services.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Components.INotebookFileSyncService>();
            await syncService.SyncNotebookImmediateAsync(notebook.Id);
        }));
        await Task.WhenAll(syncTasks);

        // Assert: exactly one row for the concurrently-created file.
        // Other notebook files may already exist (for example guide resource copies).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        int count = await db.NotebookFiles.CountAsync(nf =>
            nf.NotebookId == notebook.Id &&
            nf.RelativePath == "concurrent.txt");
        count.Should().Be(1);
    }

    [TestMethod]
    public async Task Sync_skips_locked_file_then_picks_up()
    {
        // Arrange
        var projResp = await _client.PostAsJsonAsync("/api/projects", new { title = $"p-sync-{Guid.NewGuid():N}", description = "d" });
        projResp.EnsureSuccessStatusCode();
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var nbResp = await _client.PostAsJsonAsync($"/api/projects/{proj!.Id}/notebooks", new { title = $"nb-{Guid.NewGuid():N}", guideId = await GetCreativeGuideIdAsync() });
        nbResp.EnsureSuccessStatusCode();
        var notebook = await nbResp.Content.ReadFromJsonAsync<NotebookDto>();

        var nbRoot = await GetNotebookRootPathAsync(proj!.Id, notebook!.Id);
        Directory.CreateDirectory(nbRoot);

        var filePath = Path.Combine(nbRoot, "locked.txt");
        await File.WriteAllTextAsync(filePath, "locked");

        // Open a write lock with no sharing
        await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var resp1 = await _client.PostAsync($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files/sync", content: null);
            resp1.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

            // Manually trigger sync to avoid background job timing issues
            using (var scope = _factory.Services.CreateScope())
            {
                var syncService = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Components.INotebookFileSyncService>();
                await syncService.SyncNotebookAsync(notebook.Id);
            }

            // Should be skipped, so no files
            var list1 = await _client.GetFromJsonAsync<List<NotebookFileDto>>($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files");
            list1.Should().NotBeNull();
            list1!.Should().BeEmpty();
        }

        // After releasing lock, sync again
        var resp2 = await _client.PostAsync($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files/sync", content: null);
        resp2.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

        using (var scope = _factory.Services.CreateScope())
        {
            var syncService = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Components.INotebookFileSyncService>();
            await syncService.SyncNotebookAsync(notebook.Id);
        }

        var list2 = await _client.GetFromJsonAsync<List<NotebookFileDto>>($"/api/projects/{proj.Id}/notebooks/{notebook.Id}/files");
        list2.Should().NotBeNull();
        list2!.Should().HaveCount(1);
        list2[0].RelativePath.Should().Be("locked.txt");
    }

    private static (string NotebookStorageRoot, string StorageRoot) GetNotebookRootAndStorage()
    {
        using var scope = _factory.Services.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var storageRoot = cfg["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path not configured");
        return (storageRoot, storageRoot);
    }

    private static async Task<string> GetNotebookRootPathAsync(Guid projectId, Guid notebookId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var storageRoot = cfg["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path not configured");

        var projectSlug = await db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.Slug)
            .FirstOrDefaultAsync() ?? projectId.ToString();
        var notebookSlug = await db.Notebooks
            .Where(n => n.Id == notebookId)
            .Select(n => n.Slug)
            .FirstOrDefaultAsync() ?? notebookId.ToString();

        return Path.Combine(storageRoot, projectSlug, notebookSlug);
    }
}


