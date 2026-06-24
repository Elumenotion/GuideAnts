using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Sync;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class NotebookSyncFileEnumeratorTests
{
    [TestMethod]
    public void EnumerateSyncableRelativePaths_ExcludesProjectedOutputResourceSymlink()
    {
        var notebookRoot = CreateTempNotebookRoot();
        try
        {
            var resourcesDir = Path.Combine(notebookRoot, "Resources", "crew-Slide-Shows");
            var outputDir = Path.Combine(notebookRoot, "Output");
            Directory.CreateDirectory(resourcesDir);
            Directory.CreateDirectory(outputDir);

            var resourcePath = Path.Combine(resourcesDir, "api.py");
            File.WriteAllText(resourcePath, "print('resource')");

            var projectedPath = Path.Combine(outputDir, "api.py");
            var relativeTarget = Path.Combine("..", "Resources", "crew-Slide-Shows", "api.py");
            if (!TryCreateFileSymlink(projectedPath, relativeTarget))
            {
                Assert.Inconclusive("File symlink creation is not available in this environment.");
            }

            var userVisiblePath = Path.Combine(outputDir, "user-visible.txt");
            File.WriteAllText(userVisiblePath, "visible");

            var paths = NotebookSyncFileEnumerator.EnumerateSyncableRelativePaths(notebookRoot);

            paths.Should().Contain("Resources/crew-Slide-Shows/api.py");
            paths.Should().Contain("Output/user-visible.txt");
            paths.Should().NotContain("Output/api.py");
        }
        finally
        {
            TryDeleteDirectory(notebookRoot);
        }
    }

    [TestMethod]
    public void EnumerateSyncableRelativePaths_KeepsOutputSymlink_WhenTargetOutsideResources()
    {
        var notebookRoot = CreateTempNotebookRoot();
        try
        {
            var dataDir = Path.Combine(notebookRoot, "Data");
            var outputDir = Path.Combine(notebookRoot, "Output");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(outputDir);

            var dataPath = Path.Combine(dataDir, "data.txt");
            File.WriteAllText(dataPath, "data");

            var outputLinkPath = Path.Combine(outputDir, "data.txt");
            var relativeTarget = Path.Combine("..", "Data", "data.txt");
            if (!TryCreateFileSymlink(outputLinkPath, relativeTarget))
            {
                Assert.Inconclusive("File symlink creation is not available in this environment.");
            }

            var paths = NotebookSyncFileEnumerator.EnumerateSyncableRelativePaths(notebookRoot);
            paths.Should().Contain("Output/data.txt");
        }
        finally
        {
            TryDeleteDirectory(notebookRoot);
        }
    }

    private static string CreateTempNotebookRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-enumerator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
