using FluentAssertions;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public sealed class HostMountHostPathTests
{
    [TestMethod]
    [DataRow(@"D:\repos\GuideAnts", true)]
    [DataRow(@"D:/repos/GuideAnts", true)]
    [DataRow(@"/home/user/shared", true)]
    [DataRow(@"\\server\share\data", true)]
    [DataRow(@"//server/share/data", true)]
    [DataRow("repos/GuideAnts", false)]
    [DataRow("Shared", false)]
    [DataRow("", false)]
    public void IsAbsoluteHostPath_AcceptsCrossPlatformAbsolutePaths(string path, bool expected)
    {
        HostMountHostPath.IsAbsoluteHostPath(path).Should().Be(expected);
    }

    [TestMethod]
    [DataRow(@"D:\repos\GuideAnts", "GuideAnts")]
    [DataRow(@"D:\Data\Shared Reports", "Shared Reports")]
    [DataRow(@"/Users/me/Data/Shared", "Shared")]
    [DataRow(@"\\server\share\folder", "folder")]
    public void GetLeafName_WorksForWindowsPathsOnAnyRuntime(string hostPath, string expectedLeaf)
    {
        HostMountHostPath.GetLeafName(hostPath).Should().Be(expectedLeaf);
    }
}
