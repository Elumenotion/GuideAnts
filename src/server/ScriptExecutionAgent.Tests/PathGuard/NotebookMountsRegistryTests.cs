using FluentAssertions;

namespace ScriptExecutionAgent.Tests.PathGuard;

[TestClass]
public sealed class NotebookMountsRegistryTests
{
    [TestMethod]
    public void IsUnderAnyContainerSourcePath_matches_source_and_descendants()
    {
        var notebookRoot = Path.Combine(Path.GetTempPath(), "mount-registry-test", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(notebookRoot, "HostMounts", "abc123");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(Path.Combine(notebookRoot, ".guideants"));

        File.WriteAllText(
            Path.Combine(notebookRoot, ".guideants", "mounts.json"),
            $$"""
              {
                "schemaVersion": 1,
                "mounts": [
                  {
                    "mountId": "mount-id",
                    "leafName": "Shared",
                    "linkRelativePath": "Shared",
                    "containerSourcePath": "{{source.Replace("\\", "\\\\")}}",
                    "writable": true
                  }
                ]
              }
              """);

        var (registry, status, _) = NotebookMountsRegistry.TryLoad(notebookRoot);
        status.Should().Be(MountsRegistryLoadStatus.Loaded);
        registry.Should().NotBeNull();

        registry!.IsUnderAnyContainerSourcePath(source).Should().BeTrue();
        registry.IsUnderAnyContainerSourcePath(Path.Combine(source, "nested")).Should().BeTrue();
        registry.IsUnderAnyContainerSourcePath(Path.Combine(notebookRoot, "Output")).Should().BeFalse();
    }

    [TestMethod]
    public void IsUnderAnyContainerSourcePath_returns_false_for_empty_registry()
    {
        NotebookMountsRegistry.Empty.IsUnderAnyContainerSourcePath("/app/HostMounts/anything").Should().BeFalse();
    }
}
