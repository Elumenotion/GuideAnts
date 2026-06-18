using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Tests.TestUtils;
using GuideAnts.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class NotebookFileServiceMountRootTests
{
    [TestMethod]
    public async Task DeleteAsync_RegisteredMountRoot_IsBlockedServerSide()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_mount_root_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Title = "NB",
                NotebookTemplateId = Guid.NewGuid()
            };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);

            var mountId = Guid.NewGuid();
            var mountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId);
            ctx.HostFolderMounts.Add(new HostFolderMount
            {
                Id = mountId,
                ProjectId = project.Id,
                Scope = HostFolderMountScope.Notebook,
                NotebookId = notebook.Id,
                SourceKind = SourceKind.LocalPath,
                DisplayName = "Shared",
                LeafName = "Shared",
                MountKey = mountKey,
                SourceSpec = @"D:\Data\Shared",
                ContainerSourcePath = HostFolderMountKeyDeriver.DeriveContainerSourcePath(mountKey),
                Status = HostFolderMountStatus.Active,
                CreatedByUserId = Guid.NewGuid(),
                Links =
                [
                    new HostFolderMountLink
                    {
                        NotebookId = notebook.Id,
                        LinkRelativePath = "Shared",
                        LinkPhysicalPath = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString(), "Shared"),
                        Status = HostFolderMountLinkStatus.Linked
                    }
                ]
            });
            await ctx.SaveChangesAsync();

            var notebookRoot = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString());
            Directory.CreateDirectory(notebookRoot);
            var mountRoot = Path.Combine(notebookRoot, "Shared");
            Directory.CreateDirectory(mountRoot);

            var svc = CreateService(ctx, tmpDir);

            var act = () => svc.DeleteAsync(project.Id, notebook.Id, "Shared");

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*host folder mount root*");
            Directory.Exists(mountRoot).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [TestMethod]
    public async Task DeleteAsync_FileInsideMountRoot_IsAllowed()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "wf_mount_inner_" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        try
        {
            await using var ctx = CreateContext();
            var project = new Project { Id = Guid.NewGuid(), Title = "P" };
            var notebook = new Notebook
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Title = "NB",
                NotebookTemplateId = Guid.NewGuid()
            };
            ctx.Projects.Add(project);
            ctx.Notebooks.Add(notebook);

            var mountId = Guid.NewGuid();
            var mountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId);
            ctx.HostFolderMounts.Add(new HostFolderMount
            {
                Id = mountId,
                ProjectId = project.Id,
                Scope = HostFolderMountScope.Notebook,
                NotebookId = notebook.Id,
                SourceKind = SourceKind.LocalPath,
                DisplayName = "Shared",
                LeafName = "Shared",
                MountKey = mountKey,
                SourceSpec = @"D:\Data\Shared",
                ContainerSourcePath = HostFolderMountKeyDeriver.DeriveContainerSourcePath(mountKey),
                Status = HostFolderMountStatus.Active,
                CreatedByUserId = Guid.NewGuid(),
                Links =
                [
                    new HostFolderMountLink
                    {
                        NotebookId = notebook.Id,
                        LinkRelativePath = "Shared",
                        LinkPhysicalPath = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString(), "Shared"),
                        Status = HostFolderMountLinkStatus.Linked
                    }
                ]
            });
            await ctx.SaveChangesAsync();

            var notebookRoot = Path.Combine(tmpDir, project.Id.ToString(), "notebooks", notebook.Id.ToString());
            var innerFile = Path.Combine(notebookRoot, "Shared", "inner.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(innerFile)!);
            await File.WriteAllTextAsync(innerFile, "inside mount");

            ctx.NotebookFiles.Add(new NotebookFile
            {
                Id = Guid.NewGuid(),
                NotebookId = notebook.Id,
                RelativePath = "Shared/inner.txt",
                FileSize = 13,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "abc",
                Created = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            var svc = CreateService(ctx, tmpDir);
            var ok = await svc.DeleteAsync(project.Id, notebook.Id, "Shared/inner.txt");

            ok.Should().BeTrue();
            File.Exists(innerFile).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static IConfiguration CreateConfig(string storagePath)
    {
        var dict = new Dictionary<string, string?> { ["FileStorage:Path"] = storagePath };
        return new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    private static NotebookFileService CreateService(ApplicationDbContext ctx, string storagePath)
    {
        var scopeFactory = new TestServiceScopeFactory(ctx);

        var sync = new NotebookFileSyncService(
            scopeFactory,
            CreateConfig(storagePath),
            NullLogger<NotebookFileSyncService>.Instance,
            Mock.Of<IFileLineageService>(),
            Mock.Of<IMarkdownExtractionService>(),
            Mock.Of<IUsageRecorder>(),
            Mock.Of<INotebookLockService>());

        return new NotebookFileService(
            scopeFactory,
            CreateConfig(storagePath),
            sync,
            NullLogger<NotebookFileService>.Instance,
            Mock.Of<IFileLineageService>(),
            Mock.Of<IContentFileService>(),
            Mock.Of<IMarkdownExtractionService>());
    }
}
