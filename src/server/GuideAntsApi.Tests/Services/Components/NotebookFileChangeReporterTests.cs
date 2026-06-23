using System.Reflection;
using System.Security.Cryptography;
using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Tests.Services.Components;

/// <summary>
/// Coverage for <see cref="NotebookFileChangeReporter"/>: the pure CWD path conversion across
/// private/published layouts, plus the filesystem-vs-database change detection (new/modified/
/// unchanged/temp-script-excluded). Uses a real temporary storage root and in-memory EF.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NotebookFileChangeReporterTests
{
    [TestInitialize]
    public void ResetPathHelper()
    {
        // NotebookFileChangeReporter resolves the local working directory through the static
        // NotebookPathHelper provider. Clear it so tests use the deterministic legacy fallback.
        var helperType = typeof(NotebookDockerScriptService).Assembly
            .GetType("GuideAntsApi.Services.NotebookPathHelper", throwOnError: true)!;
        var method = helperType.GetMethod("InitializeServiceProvider", BindingFlags.Public | BindingFlags.Static)!;
        method.Invoke(null, [new ServiceCollection().BuildServiceProvider()]);
    }

    [TestMethod]
    public void ToCwdRelativePath_Private_StripsOutputPrefix()
    {
        NotebookFileChangeReporter.ToCwdRelativePath("Output/chart.png", isPublished: false, runId: null)
            .Should().Be("chart.png");
    }

    [TestMethod]
    public void ToCwdRelativePath_Private_SimpleFileReturnedAsIs()
    {
        NotebookFileChangeReporter.ToCwdRelativePath("notes.txt", isPublished: false, runId: null)
            .Should().Be("notes.txt");
    }

    [TestMethod]
    public void ToCwdRelativePath_Private_NestedNonOutputGetsParentPrefix()
    {
        NotebookFileChangeReporter.ToCwdRelativePath("Data/sub/file.csv", isPublished: false, runId: null)
            .Should().Be("../Data/sub/file.csv");
    }

    [TestMethod]
    public void ToCwdRelativePath_Published_FileInRunFolderReturnsFilename()
    {
        NotebookFileChangeReporter.ToCwdRelativePath("Runs/ABC123/out.png", isPublished: true, runId: "ABC123")
            .Should().Be("out.png");
    }

    [TestMethod]
    public void ToCwdRelativePath_Published_FileElsewhereGetsParentPrefix()
    {
        NotebookFileChangeReporter.ToCwdRelativePath("Output/shared.png", isPublished: true, runId: "ABC123")
            .Should().Be("../Output/shared.png");
    }

    [TestMethod]
    public void ToCwdRelativePath_Published_NullRunIdFallsBackToPrivateRules()
    {
        NotebookFileChangeReporter.ToCwdRelativePath("Output/x.png", isPublished: true, runId: null)
            .Should().Be("x.png");
    }

    [TestMethod]
    public void ToCwdRelativePath_NormalizesBackslashes()
    {
        NotebookFileChangeReporter.ToCwdRelativePath("Output\\nested\\file.txt", isPublished: false, runId: null)
            .Should().Be("nested/file.txt");
    }

    [TestMethod]
    public void ToCwdRelativePath_EmptyReturnedUnchanged()
    {
        NotebookFileChangeReporter.ToCwdRelativePath(string.Empty, isPublished: false, runId: null)
            .Should().Be(string.Empty);
    }

    [TestMethod]
    public async Task DetectChangesAsync_ReturnsEmpty_WhenWorkingDirectoryMissing()
    {
        using var storage = new TempStorage();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var provider = BuildProvider(storage.Root, projectId, notebookId);
        var context = new InvocationContext(projectId, notebookId, Guid.NewGuid()) { IsPublished = false };

        var (newFiles, modifiedFiles) = await NotebookFileChangeReporter.DetectChangesAsync(provider, storage.Root, context);

        newFiles.Should().BeEmpty();
        modifiedFiles.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DetectChangesAsync_DetectsNewFile_NotInDatabase()
    {
        using var storage = new TempStorage();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var outputDir = storage.OutputDir(projectId, notebookId);
        File.WriteAllText(Path.Combine(outputDir, "brand-new.txt"), "hello");

        var provider = BuildProvider(storage.Root, projectId, notebookId);
        var context = new InvocationContext(projectId, notebookId, Guid.NewGuid()) { IsPublished = false };

        var (newFiles, modifiedFiles) = await NotebookFileChangeReporter.DetectChangesAsync(provider, storage.Root, context);

        newFiles.Should().ContainSingle().Which.Should().Be("brand-new.txt");
        modifiedFiles.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DetectChangesAsync_DetectsModifiedFile_WhenHashDiffers()
    {
        using var storage = new TempStorage();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var outputDir = storage.OutputDir(projectId, notebookId);
        var filePath = Path.Combine(outputDir, "changed.txt");
        File.WriteAllText(filePath, "new content");
        var info = new FileInfo(filePath);

        var provider = BuildProvider(storage.Root, projectId, notebookId, seed: ctx =>
        {
            ctx.NotebookFiles.Add(MakeFile(notebookId, "Output/changed.txt", info.Length, info.LastWriteTimeUtc, "STALEHASH"));
        });
        var context = new InvocationContext(projectId, notebookId, Guid.NewGuid()) { IsPublished = false };

        var (newFiles, modifiedFiles) = await NotebookFileChangeReporter.DetectChangesAsync(provider, storage.Root, context);

        newFiles.Should().BeEmpty();
        modifiedFiles.Should().ContainSingle().Which.Should().Be("changed.txt");
    }

    [TestMethod]
    public async Task DetectChangesAsync_IgnoresUnchangedFile()
    {
        using var storage = new TempStorage();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var outputDir = storage.OutputDir(projectId, notebookId);
        var filePath = Path.Combine(outputDir, "stable.txt");
        File.WriteAllText(filePath, "unchanged content");
        var info = new FileInfo(filePath);
        var hash = ComputeSha256(filePath);

        var provider = BuildProvider(storage.Root, projectId, notebookId, seed: ctx =>
        {
            ctx.NotebookFiles.Add(MakeFile(notebookId, "Output/stable.txt", info.Length, info.LastWriteTimeUtc, hash));
        });
        var context = new InvocationContext(projectId, notebookId, Guid.NewGuid()) { IsPublished = false };

        var (newFiles, modifiedFiles) = await NotebookFileChangeReporter.DetectChangesAsync(provider, storage.Root, context);

        newFiles.Should().BeEmpty();
        modifiedFiles.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DetectChangesAsync_ExcludesTempScriptFiles()
    {
        using var storage = new TempStorage();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var outputDir = storage.OutputDir(projectId, notebookId);
        File.WriteAllText(Path.Combine(outputDir, "script_runner.py"), "print(1)");
        File.WriteAllText(Path.Combine(outputDir, $"{Guid.NewGuid():N}_script.py"), "print(2)");
        File.WriteAllText(Path.Combine(outputDir, "keep.txt"), "real output");

        var provider = BuildProvider(storage.Root, projectId, notebookId);
        var context = new InvocationContext(projectId, notebookId, Guid.NewGuid()) { IsPublished = false };

        var (newFiles, modifiedFiles) = await NotebookFileChangeReporter.DetectChangesAsync(provider, storage.Root, context);

        newFiles.Should().ContainSingle().Which.Should().Be("keep.txt");
        modifiedFiles.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DetectChangesAsync_IgnoresOutputSymlinkProjectedFromResources()
    {
        using var storage = new TempStorage();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var notebookRoot = storage.NotebookRoot(projectId, notebookId);
        var outputDir = storage.OutputDir(projectId, notebookId);

        var resourcesDir = Path.Combine(notebookRoot, "Resources", "crew-Slide-Shows");
        Directory.CreateDirectory(resourcesDir);
        var targetPath = Path.Combine(resourcesDir, "api.py");
        File.WriteAllText(targetPath, "print('hello')");

        var symlinkPath = Path.Combine(outputDir, "api.py");
        var relativeTarget = Path.Combine("..", "Resources", "crew-Slide-Shows", "api.py");
        if (!TryCreateFileSymlink(symlinkPath, relativeTarget))
        {
            Assert.Inconclusive("File symlink creation is not available in this environment.");
        }

        var resourceInfo = new FileInfo(targetPath);
        var provider = BuildProvider(storage.Root, projectId, notebookId, seed: ctx =>
        {
            ctx.NotebookFiles.Add(MakeFile(
                notebookId,
                "Resources/crew-Slide-Shows/api.py",
                resourceInfo.Length,
                resourceInfo.LastWriteTimeUtc,
                ComputeSha256(targetPath)));
        });
        var context = new InvocationContext(projectId, notebookId, Guid.NewGuid()) { IsPublished = false };

        var (newFiles, modifiedFiles) = await NotebookFileChangeReporter.DetectChangesAsync(provider, storage.Root, context);

        newFiles.Should().BeEmpty();
        modifiedFiles.Should().BeEmpty();
    }

    private static NotebookFile MakeFile(Guid notebookId, string relativePath, long size, DateTime lastModifiedUtc, string hash)
    {
        var file = new NotebookFile
        {
            Id = Guid.NewGuid(),
            NotebookId = notebookId,
            RelativePath = relativePath,
            FileSize = size,
            LastModifiedUtc = lastModifiedUtc,
            FileHash = hash
        };
        file.GenerateDocumentId(notebookId);
        return file;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream));
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
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static IServiceProvider BuildProvider(
        string storageRoot,
        Guid projectId,
        Guid notebookId,
        Action<ApplicationDbContext>? seed = null)
    {
        var dbName = $"reporter-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ctx.Projects.Add(new Project { Id = projectId, Title = "P", Slug = "p", Created = DateTime.UtcNow });
            ctx.Notebooks.Add(new Notebook { Id = notebookId, ProjectId = projectId, Title = "N", Slug = "n", Created = DateTime.UtcNow });
            seed?.Invoke(ctx);
            ctx.SaveChanges();
        }

        return provider;
    }

    private sealed class TempStorage : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "guideants-reporter-" + Guid.NewGuid().ToString("N"));

        public TempStorage() => Directory.CreateDirectory(Root);

        public string NotebookRoot(Guid projectId, Guid notebookId)
        {
            var dir = Path.Combine(Root, projectId.ToString(), "notebooks", notebookId.ToString());
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string OutputDir(Guid projectId, Guid notebookId)
        {
            var dir = Path.Combine(NotebookRoot(projectId, notebookId), "Output");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
