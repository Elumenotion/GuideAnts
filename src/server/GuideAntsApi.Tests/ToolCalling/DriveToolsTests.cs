using AntRunner.ToolCalling.Functions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class DriveToolsTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "drivetools-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void ListDrives_ReturnsReadyDrivesWithPopulatedDetails()
    {
        var drives = DriveTools.ListDrives();

        drives.Should().NotBeNull();
        drives.Should().NotBeEmpty("the machine running the tests has at least one ready drive");
        drives.Should().OnlyContain(d => d.IsReady);
        drives.Should().OnlyContain(d => !string.IsNullOrEmpty(d.Name));
        drives.Should().OnlyContain(d => !string.IsNullOrEmpty(d.DriveType));
        drives.Should().OnlyContain(d => d.TotalSize >= 0);
    }

    [TestMethod]
    public void ListItems_NonRecursive_ReturnsTopLevelFilesAndDirectories()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(_root, "file.txt");
        File.WriteAllText(filePath, "hello");
        File.WriteAllText(Path.Combine(subDir, "nested.txt"), "nested-content");

        var items = DriveTools.ListItems(_root, recurse: false);

        var directory = items.Should().ContainSingle(i => i.IsDirectory).Subject;
        directory.Name.Should().Be("sub");
        directory.Path.Should().Be(new DirectoryInfo(subDir).FullName);
        directory.Size.Should().BeNull();

        var file = items.Should().ContainSingle(i => !i.IsDirectory).Subject;
        file.Name.Should().Be("file.txt");
        file.Path.Should().Be(new FileInfo(filePath).FullName);
        file.Size.Should().Be(5);
    }

    [TestMethod]
    public void ListItems_Recursive_IncludesNestedEntries()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(_root, "top.txt"), "top");
        File.WriteAllText(Path.Combine(subDir, "nested.txt"), "nested");

        var items = DriveTools.ListItems(_root, recurse: true);

        items.Should().Contain(i => i.Name == "nested.txt" && !i.IsDirectory);
        items.Should().Contain(i => i.Name == "top.txt" && !i.IsDirectory);
        items.Should().Contain(i => i.Name == "sub" && i.IsDirectory);
    }

    [TestMethod]
    public void ListItems_WithSearchPattern_FiltersResults()
    {
        File.WriteAllText(Path.Combine(_root, "keep.md"), "a");
        File.WriteAllText(Path.Combine(_root, "skip.txt"), "b");

        var items = DriveTools.ListItems(_root, recurse: false, searchPattern: "*.md");

        items.Should().ContainSingle();
        items[0].Name.Should().Be("keep.md");
    }

    [TestMethod]
    public void ListItems_EmptyDirectory_ReturnsEmptyList()
    {
        var items = DriveTools.ListItems(_root, recurse: false);

        items.Should().BeEmpty();
    }
}
