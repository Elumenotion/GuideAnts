using FluentAssertions;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public class HostMountDirectoryScannerTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "ga-mount-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var d3 = Path.Combine(_root, "d1", "d2", "d3");
        Directory.CreateDirectory(Path.Combine(d3, "audiocpp"));
        Directory.CreateDirectory(Path.Combine(d3, "audiocpp-asr"));
        File.WriteAllText(Path.Combine(d3, "README.md"), "readme");
        File.WriteAllText(Path.Combine(d3, "audiocpp", "SKILL.md"), "skill");
        File.WriteAllText(Path.Combine(d3, "audiocpp-asr", "SKILL.md"), "asr");
        HostMountListingCache.ClearAll();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }

        HostMountListingCache.ClearAll();
    }

    [TestMethod]
    public void Scan_Depth3_IncludesReadme_ExcludesDepth4Files()
    {
        var result = HostMountDirectoryScanner.Scan(
            [new HostMountDirectoryScanner.MountRoot("leaf", _root)],
            maxFiles: 5000,
            maxDepth: 3,
            scanBudget: TimeSpan.FromSeconds(5));

        result.Files.Select(f => f.RelativePath).Should().Contain("leaf/d1/d2/d3/README.md");
        result.Files.Select(f => f.RelativePath).Should().NotContain("leaf/d1/d2/d3/audiocpp/SKILL.md");
        result.Directories.Select(d => d.RelativePath).Should().Contain("leaf/d1/d2/d3");
        result.Directories.Select(d => d.RelativePath).Should().NotContain("leaf/d1/d2/d3/audiocpp");
    }

    [TestMethod]
    public void ListLevel_ReturnsImmediateChildrenOnly()
    {
        var result = HostMountDirectoryScanner.ListLevel(
            _root,
            relativePathPrefix: "leaf",
            relativeWithinMount: "d1/d2/d3",
            maxFiles: 5000,
            scanBudget: TimeSpan.FromSeconds(5));

        result.Files.Select(f => f.RelativePath).Should().ContainSingle("leaf/d1/d2/d3/README.md");
        result.Directories.Select(d => d.Name).Should().BeEquivalentTo("audiocpp", "audiocpp-asr");
        result.Files.Select(f => f.RelativePath).Should().NotContain("leaf/d1/d2/d3/audiocpp/SKILL.md");
    }

    [TestMethod]
    public void ListingCache_ReturnsWarmEntry_UntilInvalidated()
    {
        var mountKey = Guid.NewGuid().ToString("N");
        var key = HostMountListingCache.ShallowKey(mountKey);
        var scan = HostMountDirectoryScanner.Scan(
            [new HostMountDirectoryScanner.MountRoot("leaf", _root)],
            maxFiles: 5000,
            maxDepth: 3,
            scanBudget: TimeSpan.FromSeconds(5));

        HostMountListingCache.Set(key, scan, TimeSpan.FromMinutes(2));
        HostMountListingCache.TryGet(key, out var cached).Should().BeTrue();
        cached.Files.Count.Should().Be(scan.Files.Count);

        HostMountListingCache.InvalidateMount(mountKey);
        HostMountListingCache.TryGet(key, out _).Should().BeFalse();
    }
}
