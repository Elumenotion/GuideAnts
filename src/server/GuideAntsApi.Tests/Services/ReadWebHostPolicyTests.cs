using FluentAssertions;
using GuideAntsApi.Services;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class ReadWebHostPolicyTests
{
    [TestMethod]
    [DataRow("github.com", true)]
    [DataRow("api.github.com", true)]
    [DataRow("raw.githubusercontent.com", true)]
    [DataRow("gist.github.com", true)]
    [DataRow("docs.github.com", true)]
    [DataRow("example.com", false)]
    public void IsAutoExclusionProtected_RecognizesGitHubHosts(string host, bool expected)
    {
        ReadWebHostPolicy.IsAutoExclusionProtected(host).Should().Be(expected);
    }
}
