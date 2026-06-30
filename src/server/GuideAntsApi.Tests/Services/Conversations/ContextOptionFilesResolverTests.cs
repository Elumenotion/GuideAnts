using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.UserProjectContextOptions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ContextOptionFilesResolverTests
{
    [TestMethod]
    public async Task ResolvePathsAsync_IncludesOutputSymlinks_AndLinkedMountRootContentsOnly()
    {
        var storageRoot = CreateTempDirectory();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var notebookRoot = Path.Combine(storageRoot, projectId.ToString(), "notebooks", notebookId.ToString());
        Directory.CreateDirectory(notebookRoot);

        var resourcesDir = Path.Combine(notebookRoot, "Resources", "crew-Slide-Shows");
        var outputDir = Path.Combine(notebookRoot, "Output");
        Directory.CreateDirectory(resourcesDir);
        Directory.CreateDirectory(outputDir);

        var resourcePath = Path.Combine(resourcesDir, "api.py");
        await File.WriteAllTextAsync(resourcePath, "print('resource')");
        var projectedPath = Path.Combine(outputDir, "api.py");
        var relativeTarget = Path.Combine("..", "Resources", "crew-Slide-Shows", "api.py");
        if (!TryCreateFileSymlink(projectedPath, relativeTarget))
        {
            Assert.Inconclusive("File symlink creation is not available in this environment.");
        }

        var mountSource = Path.Combine(storageRoot, "host-source");
        Directory.CreateDirectory(Path.Combine(mountSource, "nested"));
        await File.WriteAllTextAsync(Path.Combine(mountSource, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(mountSource, "nested", "deep.txt"), "deep");

        var mountLinkPath = Path.Combine(notebookRoot, "Shared");
        if (!TryCreateDirectorySymlink(mountLinkPath, mountSource))
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        await using var db = CreateDbContext();
        SeedLinkedMount(db, projectId, notebookId, mountLinkPath);

        var resourceFile = new NotebookFile
        {
            NotebookId = notebookId,
            RelativePath = "Resources/crew-Slide-Shows/api.py",
            FileSize = new FileInfo(resourcePath).Length,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash-resource"
        };
        resourceFile.GenerateDocumentId(notebookId);
        db.NotebookFiles.Add(resourceFile);
        await db.SaveChangesAsync();

        var paths = await ContextOptionFilesResolver.ResolvePathsAsync(
            db,
            new LegacyStoragePathResolver(storageRoot),
            projectId,
            notebookId,
            isPublished: false);

        paths.Should().Contain("api.py");
        paths.Should().NotContain("../Resources/crew-Slide-Shows/api.py");
        paths.Should().Contain("../Shared/root.txt");
        paths.Should().Contain("../Shared/nested/");
        paths.Should().NotContain("../Shared/nested/deep.txt");
    }

    [TestMethod]
    public async Task ResolveAsync_FilesContextOption_IncludesLinkedMountShallowListing()
    {
        var storageRoot = CreateTempDirectory();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var notebookRoot = Path.Combine(storageRoot, projectId.ToString(), "notebooks", notebookId.ToString());
        Directory.CreateDirectory(notebookRoot);

        var mountSource = Path.Combine(storageRoot, "host-source");
        Directory.CreateDirectory(Path.Combine(mountSource, "docs"));
        await File.WriteAllTextAsync(Path.Combine(mountSource, "readme.txt"), "hello");

        var mountLinkPath = Path.Combine(notebookRoot, "Shared");
        if (!TryCreateDirectorySymlink(mountLinkPath, mountSource))
        {
            Assert.Inconclusive("Directory symlink creation is not available in this environment.");
        }

        await using var db = CreateDbContext();
        SeedLinkedMount(db, projectId, notebookId, mountLinkPath);

        var service = CreateService(db, new LegacyStoragePathResolver(storageRoot));
        var assistant = new AssistantDefinition
        {
            ContextOptions = new Dictionary<string, string>
            {
                ["files"] = "[@files]"
            }
        };

        var resolved = await service.ResolveAsync(assistant, projectId, notebookId, Guid.NewGuid());

        resolved["files"].Should().Contain("../Shared/readme.txt");
        resolved["files"].Should().Contain("../Shared/docs/");
    }

    [TestMethod]
    public async Task ResolveAsync_FilesContextOption_ExcludesNpmArtifactPathsFromDatabase()
    {
        await using var db = CreateDbContext();
        var notebookId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var npmFile = new NotebookFile
        {
            NotebookId = notebookId,
            RelativePath = "Output/.npm/_cacache/content-v2/sha512/ab/cd/cache-entry",
            FileSize = 128,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash-npm"
        };
        npmFile.GenerateDocumentId(notebookId);
        var userFile = new NotebookFile
        {
            NotebookId = notebookId,
            RelativePath = "Output/result.txt",
            FileSize = 64,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash-result"
        };
        userFile.GenerateDocumentId(notebookId);
        db.NotebookFiles.AddRange(npmFile, userFile);
        await db.SaveChangesAsync();

        var service = CreateService(db, new LegacyStoragePathResolver(Path.Combine(Path.GetTempPath(), "context-option-files-" + Guid.NewGuid().ToString("N"))));
        var assistant = new AssistantDefinition
        {
            ContextOptions = new Dictionary<string, string>
            {
                ["files"] = "[@files]"
            }
        };

        var resolved = await service.ResolveAsync(assistant, projectId, notebookId, Guid.NewGuid());

        resolved["files"].Should().Contain("result.txt");
        resolved["files"].Should().NotContain(".npm");
    }

    private static ContextOptionsService CreateService(ApplicationDbContext db, IStoragePathResolver pathResolver)
    {
        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService
            .Setup(x => x.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((CurrentUserContext?)null);

        var userOptionsService = new Mock<IUserProjectContextOptionsService>();
        userOptionsService
            .Setup(x => x.GetOptionsAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        return new ContextOptionsService(
            db,
            currentUserService.Object,
            userOptionsService.Object,
            pathResolver);
    }

    private static void SeedLinkedMount(ApplicationDbContext db, Guid projectId, Guid notebookId, string mountLinkPath)
    {
        var mountId = Guid.NewGuid();
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
        db.HostFolderMounts.Add(new HostFolderMount
        {
            Id = mountId,
            ProjectId = projectId,
            NotebookId = notebookId,
            Scope = HostFolderMountScope.Notebook,
            SourceKind = SourceKind.LocalPath,
            DisplayName = "Shared",
            LeafName = "Shared",
            MountKey = "mount-key",
            SourceSpec = mountLinkPath,
            ContainerSourcePath = "/app/HostMounts/mount-key",
            Status = HostFolderMountStatus.Active,
            CreatedByUserId = Guid.NewGuid(),
            Links =
            [
                new HostFolderMountLink
                {
                    NotebookId = notebookId,
                    LinkRelativePath = "Shared",
                    LinkPhysicalPath = mountLinkPath,
                    Status = HostFolderMountLinkStatus.Linked
                }
            ]
        });
        db.SaveChanges();
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"context-option-files-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "context-option-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool TryCreateFileSymlink(string symlinkPath, string targetPath)
    {
        try
        {
            if (File.Exists(symlinkPath))
            {
                File.Delete(symlinkPath);
            }

            File.CreateSymbolicLink(symlinkPath, targetPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymlink(string symlinkPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(symlinkPath))
            {
                Directory.Delete(symlinkPath);
            }

            Directory.CreateSymbolicLink(symlinkPath, targetPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
