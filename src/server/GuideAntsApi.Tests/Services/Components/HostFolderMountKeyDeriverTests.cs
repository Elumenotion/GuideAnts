using FluentAssertions;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public sealed class HostFolderMountKeyDeriverTests
{
    [TestMethod]
    public void DeriveMountKey_ProducesLowercaseGuidWithoutDashes()
    {
        var mountId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        var key = HostFolderMountKeyDeriver.DeriveMountKey(mountId);

        key.Should().Be("a1b2c3d4e5f67890abcdef1234567890");
        key.Should().MatchRegex("^[a-z0-9]+$");
    }

    [TestMethod]
    public void DeriveContainerSourcePath_UsesMountKey()
    {
        HostFolderMountKeyDeriver.DeriveContainerSourcePath("shared-8f3a2c")
            .Should().Be("/app/HostMounts/shared-8f3a2c");
    }

    [TestMethod]
    [DataRow(@"D:\Data\Shared Reports", "Shared Reports")]
    [DataRow(@"/Users/me/Data/Shared", "Shared")]
    public void DeriveDefaultLeafName_UsesHostFolderLeaf(string hostPath, string expectedLeaf)
    {
        HostFolderMountKeyDeriver.DeriveDefaultLeafName(hostPath).Should().Be(expectedLeaf);
    }

    [TestMethod]
    public void DeriveDefaultLeafName_EmptyPath_ReturnsEmpty()
    {
        HostFolderMountKeyDeriver.DeriveDefaultLeafName("").Should().BeEmpty();
        HostFolderMountKeyDeriver.DeriveDefaultLeafName("   ").Should().BeEmpty();
    }
}
