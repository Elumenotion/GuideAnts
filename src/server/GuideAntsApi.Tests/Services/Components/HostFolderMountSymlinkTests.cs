using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public sealed class HostFolderMountSymlinkTests
{
    [TestMethod]
    public async Task CreateSymlinksForMountAsync_CreatesLinkUnderNotebookRoot_WhenSourcePresent()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var storageRoot = CreateTempDirectory();
        var hostMountsRoot = Path.Combine(storageRoot, "host-mounts");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var mountId = Guid.NewGuid();
        var mountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId);
        var notebookRoot = Path.Combine(storageRoot, "proj", "nb");
        Directory.CreateDirectory(notebookRoot);

        var sourcePath = Path.Combine(hostMountsRoot, mountKey);
        Directory.CreateDirectory(sourcePath);
        File.WriteAllText(Path.Combine(sourcePath, "host-content.txt"), "keep");

        await using var db = CreateContext();
        SeedNotebook(db, projectId, notebookId);
        SeedMount(db, mountId, projectId, notebookId, mountKey, notebookRoot, HostFolderMountLinkStatus.PendingRestart);

        var service = CreateService(db, storageRoot, hostMountsRoot, projectId, notebookId, notebookRoot);

        await service.CreateSymlinksForMountAsync(mountId);

        var linkPath = Path.Combine(notebookRoot, "Shared");
        Directory.Exists(linkPath).Should().BeTrue();
        File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue();

        var link = await db.HostFolderMountLinks.SingleAsync();
        link.Status.Should().Be(HostFolderMountLinkStatus.Linked);
        link.LinkPhysicalPath.Should().Be(linkPath);

        var mount = await db.HostFolderMounts.SingleAsync();
        mount.Status.Should().Be(HostFolderMountStatus.Active);

        var registryPath = Path.Combine(notebookRoot, ".guideants", "mounts.json");
        File.Exists(registryPath).Should().BeTrue();
        var registryJson = await File.ReadAllTextAsync(registryPath);
        registryJson.Should().NotContain(@"D:\Data");
        registryJson.Should().NotContain("password");
        registryJson.Should().NotContain("CredentialRef");

        using var registry = JsonDocument.Parse(registryJson);
        registry.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        var mounts = registry.RootElement.GetProperty("mounts");
        mounts.GetArrayLength().Should().Be(1);
        mounts[0].GetProperty("mountId").GetGuid().Should().Be(mountId);
        mounts[0].GetProperty("leafName").GetString().Should().Be("Shared");
        mounts[0].GetProperty("linkRelativePath").GetString().Should().Be("Shared");
        mounts[0].GetProperty("containerSourcePath").GetString()
            .Should().Be(HostFolderMountKeyDeriver.DeriveContainerSourcePath(mountKey));
        mounts[0].GetProperty("writable").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public async Task CreateSymlinksForMountAsync_SetsLinkErrorAndSurfacesError_WhenSourceAbsent()
    {
        var storageRoot = CreateTempDirectory();
        var hostMountsRoot = Path.Combine(storageRoot, "host-mounts");
        Directory.CreateDirectory(hostMountsRoot);
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var mountId = Guid.NewGuid();
        var mountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId);
        var notebookRoot = Path.Combine(storageRoot, "proj", "nb");
        Directory.CreateDirectory(notebookRoot);

        await using var db = CreateContext();
        SeedNotebook(db, projectId, notebookId);
        SeedMount(db, mountId, projectId, notebookId, mountKey, notebookRoot, HostFolderMountLinkStatus.PendingRestart);

        var service = CreateService(db, storageRoot, hostMountsRoot, projectId, notebookId, notebookRoot);

        var act = () => service.CreateSymlinksForMountAsync(mountId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to create one or more mount symlinks*");

        var link = await db.HostFolderMountLinks.SingleAsync();
        link.Status.Should().Be(HostFolderMountLinkStatus.LinkError);
        link.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(Path.Combine(notebookRoot, "Shared")).Should().BeFalse();
    }

    [TestMethod]
    public async Task RemoveSymlinksForMountAsync_PreservesHostSourceContent()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var storageRoot = CreateTempDirectory();
        var hostMountsRoot = Path.Combine(storageRoot, "host-mounts");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var mountId = Guid.NewGuid();
        var mountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId);
        var notebookRoot = Path.Combine(storageRoot, "proj", "nb");
        Directory.CreateDirectory(notebookRoot);

        var sourcePath = Path.Combine(hostMountsRoot, mountKey);
        Directory.CreateDirectory(sourcePath);
        var hostFile = Path.Combine(sourcePath, "host-content.txt");
        File.WriteAllText(hostFile, "preserve-me");

        await using var db = CreateContext();
        SeedNotebook(db, projectId, notebookId);
        SeedMount(db, mountId, projectId, notebookId, mountKey, notebookRoot, HostFolderMountLinkStatus.PendingRestart);

        var service = CreateService(db, storageRoot, hostMountsRoot, projectId, notebookId, notebookRoot);
        await service.CreateSymlinksForMountAsync(mountId);

        await service.RemoveSymlinksForMountAsync(mountId);

        Directory.Exists(Path.Combine(notebookRoot, "Shared")).Should().BeFalse();
        File.Exists(hostFile).Should().BeTrue();
        File.ReadAllText(hostFile).Should().Be("preserve-me");

        var link = await db.HostFolderMountLinks.SingleAsync();
        link.Status.Should().Be(HostFolderMountLinkStatus.Unlinked);

        var registryPath = Path.Combine(notebookRoot, ".guideants", "mounts.json");
        File.Exists(registryPath).Should().BeTrue();
        using var registry = JsonDocument.Parse(await File.ReadAllTextAsync(registryPath));
        registry.RootElement.GetProperty("mounts").GetArrayLength().Should().Be(0);
    }

    [TestMethod]
    public void HostMountSourcePath_RejectsContainerPathOutsideHostMountsRoot()
    {
        HostMountSourcePath.TryResolvePhysicalSourcePath(
                @"C:\temp\host-mounts",
                "/app/ContentFiles/not-a-mount",
                out _)
            .Should().BeFalse();
    }

    [TestMethod]
    public void HostMountSourcePath_ResolvesMountKeyUnderConfiguredRoot()
    {
        var hostMountsRoot = Path.Combine(CreateTempDirectory(), "host-mounts");
        var mountKey = HostFolderMountKeyDeriver.DeriveMountKey(Guid.NewGuid());
        var containerSourcePath = HostFolderMountKeyDeriver.DeriveContainerSourcePath(mountKey);

        HostMountSourcePath.TryResolvePhysicalSourcePath(hostMountsRoot, containerSourcePath, out var physicalPath)
            .Should().BeTrue();

        physicalPath.Should().Be(Path.GetFullPath(Path.Combine(hostMountsRoot, mountKey)));
    }

    private static HostFolderMountService CreateService(
        ApplicationDbContext db,
        string storageRoot,
        string hostMountsRoot,
        Guid projectId,
        Guid notebookId,
        string notebookRoot)
    {
        var pathResolver = new Mock<IStoragePathResolver>();
        pathResolver.Setup(r => r.GetStorageRoot()).Returns(storageRoot);
        pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookId)).Returns(notebookRoot);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:HostMountsRoot"] = hostMountsRoot
            })
            .Build();

        return new HostFolderMountService(
            new TestServiceScopeFactory(db, configuration),
            pathResolver.Object,
            Microsoft.Extensions.Options.Options.Create(new GuideAntsRuntimeOptions()),
            configuration);
    }

    private static void SeedNotebook(ApplicationDbContext db, Guid projectId, Guid notebookId)
    {
        db.Projects.Add(new Project
        {
            Id = projectId,
            Title = "Project",
            Slug = "project"
        });
        db.Notebooks.Add(new Notebook
        {
            Id = notebookId,
            ProjectId = projectId,
            Title = "Notebook",
            Slug = "notebook"
        });
    }

    private static void SeedMount(
        ApplicationDbContext db,
        Guid mountId,
        Guid projectId,
        Guid notebookId,
        string mountKey,
        string notebookRoot,
        HostFolderMountLinkStatus linkStatus)
    {
        db.HostFolderMounts.Add(new HostFolderMount
        {
            Id = mountId,
            ProjectId = projectId,
            NotebookId = notebookId,
            Scope = HostFolderMountScope.Notebook,
            SourceKind = SourceKind.LocalPath,
            DisplayName = "Shared",
            LeafName = "Shared",
            MountKey = mountKey,
            SourceSpec = @"D:\Data\Shared",
            ContainerSourcePath = HostFolderMountKeyDeriver.DeriveContainerSourcePath(mountKey),
            Status = HostFolderMountStatus.PendingRestart,
            CreatedByUserId = Guid.NewGuid(),
            Links =
            [
                new HostFolderMountLink
                {
                    NotebookId = notebookId,
                    LinkRelativePath = "Shared",
                    LinkPhysicalPath = Path.Combine(notebookRoot, "Shared"),
                    Status = linkStatus
                }
            ]
        });
        db.SaveChanges();
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hfm_symlink_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool CanCreateDirectorySymlinks()
    {
        var baseDir = CreateTempDirectory();
        var targetDir = Path.Combine(baseDir, "target");
        var linkDir = Path.Combine(baseDir, "link");
        Directory.CreateDirectory(targetDir);

        try
        {
            Directory.CreateSymbolicLink(linkDir, targetDir);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(linkDir))
                {
                    Directory.Delete(linkDir);
                }

                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir);
                }

                if (Directory.Exists(baseDir))
                {
                    Directory.Delete(baseDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
