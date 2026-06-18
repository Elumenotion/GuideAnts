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
using Moq;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public sealed class HostFolderMountRemoveFlowTests
{
    [TestMethod]
    public async Task BeginRemoveMountAsync_UnlinksAllNotebooks_UpdatesRegistry_ReturnsRemoveCommand()
    {
        if (!CanCreateDirectorySymlinks())
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        var storageRoot = CreateTempDirectory();
        var hostMountsRoot = Path.Combine(storageRoot, "host-mounts");
        var projectId = Guid.NewGuid();
        var notebookA = Guid.NewGuid();
        var notebookB = Guid.NewGuid();
        var mountId = Guid.NewGuid();
        var mountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId);
        var notebookRootA = Path.Combine(storageRoot, "proj", "nb-a");
        var notebookRootB = Path.Combine(storageRoot, "proj", "nb-b");
        Directory.CreateDirectory(notebookRootA);
        Directory.CreateDirectory(notebookRootB);

        var sourcePath = Path.Combine(hostMountsRoot, mountKey);
        Directory.CreateDirectory(sourcePath);
        File.WriteAllText(Path.Combine(sourcePath, "host-content.txt"), "keep");

        await using var db = CreateContext();
        SeedProjectWithNotebooks(db, projectId, notebookA, notebookB);
        SeedProjectScopeMount(
            db,
            mountId,
            projectId,
            mountKey,
            [
                (notebookA, notebookRootA),
                (notebookB, notebookRootB)
            ]);

        var service = CreateService(db, storageRoot, hostMountsRoot, projectId, notebookA, notebookRootA, notebookB, notebookRootB);
        await service.CreateSymlinksForMountAsync(mountId);

        var result = await service.BeginRemoveMountAsync(projectId, mountId);

        result.Status.Should().Be(HostFolderMountStatus.PendingRemoval);
        result.RemoveCommand.Should().Contain("remove");
        result.RemoveCommand.Should().Contain(mountId.ToString());

        Directory.Exists(Path.Combine(notebookRootA, "Shared")).Should().BeFalse();
        Directory.Exists(Path.Combine(notebookRootB, "Shared")).Should().BeFalse();
        File.Exists(Path.Combine(sourcePath, "host-content.txt")).Should().BeTrue();

        var links = await db.HostFolderMountLinks.Where(l => l.HostFolderMountId == mountId).ToListAsync();
        links.Should().HaveCount(2);
        links.Should().OnlyContain(l => l.Status == HostFolderMountLinkStatus.Unlinked);

        foreach (var root in new[] { notebookRootA, notebookRootB })
        {
            var registryPath = Path.Combine(root, ".guideants", "mounts.json");
            File.Exists(registryPath).Should().BeTrue();
            using var registry = JsonDocument.Parse(await File.ReadAllTextAsync(registryPath));
            registry.RootElement.GetProperty("mounts").GetArrayLength().Should().Be(0);
        }
    }

    [TestMethod]
    public async Task BeginRemoveMountAsync_WhenSymlinkRemovalFails_StaysErrorWithRemediation()
    {
        var storageRoot = CreateTempDirectory();
        var hostMountsRoot = Path.Combine(storageRoot, "host-mounts");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var mountId = Guid.NewGuid();
        var mountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId);
        var notebookRoot = Path.Combine(storageRoot, "proj", "nb");
        Directory.CreateDirectory(notebookRoot);

        var linkPath = Path.Combine(notebookRoot, "Shared");
        Directory.CreateDirectory(linkPath);
        File.WriteAllText(Path.Combine(linkPath, "blocking.txt"), "not-a-symlink");

        await using var db = CreateContext();
        SeedProjectWithNotebooks(db, projectId, notebookId);
        SeedProjectScopeMount(
            db,
            mountId,
            projectId,
            mountKey,
            [(notebookId, notebookRoot)],
            linkStatus: HostFolderMountLinkStatus.Linked,
            mountStatus: HostFolderMountStatus.Active);

        var service = CreateService(db, storageRoot, hostMountsRoot, projectId, notebookId, notebookRoot);

        var act = () => service.BeginRemoveMountAsync(projectId, mountId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to remove one or more notebook symlinks*");

        var mount = await db.HostFolderMounts.SingleAsync(m => m.Id == mountId);
        mount.Status.Should().Be(HostFolderMountStatus.Error);
        mount.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        mount.Status.Should().NotBe(HostFolderMountStatus.Removed);

        var link = await db.HostFolderMountLinks.SingleAsync();
        link.Status.Should().Be(HostFolderMountLinkStatus.UnlinkError);
        Directory.Exists(linkPath).Should().BeTrue();
    }

    [TestMethod]
    public async Task ReconcileMountAsync_PendingRemovalWithSourceGone_MarksRemovedAndClearsStaleSymlinks()
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

        await using var db = CreateContext();
        SeedProjectWithNotebooks(db, projectId, notebookId);
        SeedProjectScopeMount(
            db,
            mountId,
            projectId,
            mountKey,
            [(notebookId, notebookRoot)],
            linkStatus: HostFolderMountLinkStatus.Linked,
            mountStatus: HostFolderMountStatus.PendingRemoval);

        var service = CreateService(db, storageRoot, hostMountsRoot, projectId, notebookId, notebookRoot);
        var linkPath = Path.Combine(notebookRoot, "Shared");
        Directory.CreateSymbolicLink(linkPath, HostFolderMountKeyDeriver.DeriveContainerSourcePath(mountKey));

        Directory.Delete(sourcePath, recursive: true);

        var result = await service.ReconcileMountAsync(projectId, mountId);

        result.Status.Should().Be(HostFolderMountStatus.Removed);
        Directory.Exists(linkPath).Should().BeFalse();

        var mount = await db.HostFolderMounts.SingleAsync(m => m.Id == mountId);
        mount.RemovedUtc.Should().NotBeNull();
        var link = await db.HostFolderMountLinks.SingleAsync();
        link.Status.Should().Be(HostFolderMountLinkStatus.Unlinked);
    }

    [TestMethod]
    public async Task ReconcileMountAsync_PendingRemovalWithSourceStillPresent_StaysPendingRemoval()
    {
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

        await using var db = CreateContext();
        SeedProjectWithNotebooks(db, projectId, notebookId);
        SeedProjectScopeMount(
            db,
            mountId,
            projectId,
            mountKey,
            [(notebookId, notebookRoot)],
            linkStatus: HostFolderMountLinkStatus.Unlinked,
            mountStatus: HostFolderMountStatus.PendingRemoval);

        var service = CreateService(db, storageRoot, hostMountsRoot, projectId, notebookId, notebookRoot);

        var result = await service.ReconcileMountAsync(projectId, mountId);

        result.Status.Should().Be(HostFolderMountStatus.PendingRemoval);
        var mount = await db.HostFolderMounts.SingleAsync(m => m.Id == mountId);
        mount.Status.Should().Be(HostFolderMountStatus.PendingRemoval);
        mount.RemovedUtc.Should().BeNull();
    }

    private static HostFolderMountService CreateService(
        ApplicationDbContext db,
        string storageRoot,
        string hostMountsRoot,
        Guid projectId,
        Guid notebookAId,
        string notebookRootA,
        Guid? notebookBId = null,
        string? notebookRootB = null)
    {
        var pathResolver = new Mock<IStoragePathResolver>();
        pathResolver.Setup(r => r.GetStorageRoot()).Returns(storageRoot);
        pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookAId)).Returns(notebookRootA);
        if (notebookBId is Guid nbB && notebookRootB != null)
        {
            pathResolver.Setup(r => r.GetNotebookRootPath(projectId, nbB)).Returns(notebookRootB);
        }

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

    private static void SeedProjectWithNotebooks(
        ApplicationDbContext db,
        Guid projectId,
        params Guid[] notebookIds)
    {
        db.Projects.Add(new Project
        {
            Id = projectId,
            Title = "Project",
            Slug = "project"
        });

        var index = 0;
        foreach (var notebookId in notebookIds)
        {
            index++;
            db.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = $"Notebook {index}",
                Slug = $"notebook-{index}"
            });
        }

        db.SaveChanges();
    }

    private static void SeedProjectScopeMount(
        ApplicationDbContext db,
        Guid mountId,
        Guid projectId,
        string mountKey,
        IReadOnlyList<(Guid NotebookId, string NotebookRoot)> notebooks,
        HostFolderMountLinkStatus linkStatus = HostFolderMountLinkStatus.PendingRestart,
        HostFolderMountStatus mountStatus = HostFolderMountStatus.PendingRestart)
    {
        var mount = new HostFolderMount
        {
            Id = mountId,
            ProjectId = projectId,
            Scope = HostFolderMountScope.Project,
            SourceKind = SourceKind.LocalPath,
            DisplayName = "Shared",
            LeafName = "Shared",
            MountKey = mountKey,
            SourceSpec = @"D:\Data\Shared",
            ContainerSourcePath = HostFolderMountKeyDeriver.DeriveContainerSourcePath(mountKey),
            Status = mountStatus,
            CreatedByUserId = Guid.NewGuid()
        };

        foreach (var (notebookId, notebookRoot) in notebooks)
        {
            mount.Links.Add(new HostFolderMountLink
            {
                NotebookId = notebookId,
                LinkRelativePath = "Shared",
                LinkPhysicalPath = Path.Combine(notebookRoot, "Shared"),
                Status = linkStatus
            });
        }

        db.HostFolderMounts.Add(mount);
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
        var dir = Path.Combine(Path.GetTempPath(), "hfm_remove_" + Guid.NewGuid().ToString("N"));
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
