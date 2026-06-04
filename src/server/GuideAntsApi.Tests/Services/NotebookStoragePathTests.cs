using FluentAssertions;
using GuideAntsApi.Services;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class NotebookStoragePathTests
{
    [TestMethod]
    public void TryResolveUnderRoot_AllowsExpectedRelativePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wf-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            NotebookStoragePath.TryResolveUnderRoot(root, "Output/foo.png", out var outputPath).Should().BeTrue();
            outputPath.Should().Be(Path.GetFullPath(Path.Combine(root, "Output", "foo.png")));

            NotebookStoragePath.TryResolveUnderRoot(root, "a/b/c.txt", out var nestedPath).Should().BeTrue();
            nestedPath.Should().Be(Path.GetFullPath(Path.Combine(root, "a", "b", "c.txt")));

            NotebookStoragePath.TryResolveUnderRoot(root, string.Empty, out var rootPath).Should().BeTrue();
            rootPath.Should().Be(Path.GetFullPath(root));

            NotebookStoragePath.TryResolveUnderRoot(root, "Resources/crew-x/y", out var resourcesPath).Should().BeTrue();
            resourcesPath.Should().Be(Path.GetFullPath(Path.Combine(root, "Resources", "crew-x", "y")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TryResolveUnderRoot_RejectsTraversalAndRootedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wf-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            NotebookStoragePath.TryResolveUnderRoot(root, "../../etc/passwd", out _).Should().BeFalse();
            NotebookStoragePath.TryResolveUnderRoot(root, @"..\..\x", out _).Should().BeFalse();
            NotebookStoragePath.TryResolveUnderRoot(root, "uploads/../../other/x", out _).Should().BeFalse();
            NotebookStoragePath.TryResolveUnderRoot(root, @"C:\Windows\x", out _).Should().BeFalse();
            NotebookStoragePath.TryResolveUnderRoot(root, "/etc/passwd", out _).Should().BeFalse();
            NotebookStoragePath.TryResolveUnderRoot(root, "bad\0name.txt", out _).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SanitizeFileName_NormalizesAndRejectsInvalidNames()
    {
        NotebookStoragePath.SanitizeFileName(@"..\..\evil.txt").Should().Be("evil.txt");
        NotebookStoragePath.SanitizeFileName("..").Should().BeNull();
        NotebookStoragePath.SanitizeFileName(".").Should().BeNull();
        NotebookStoragePath.SanitizeFileName(string.Empty).Should().BeNull();
    }
}
