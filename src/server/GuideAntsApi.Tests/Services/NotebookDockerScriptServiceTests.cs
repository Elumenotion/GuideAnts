using FluentAssertions;
using GuideAntsApi.Services;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class NotebookDockerScriptServiceTests
{
    [TestMethod]
    public void BuildEndpointUri_PreservesSandboxPrefix()
    {
        var uri = NotebookDockerScriptService.BuildEndpointUri("http://localhost:8110/sandbox", "execute");

        uri.ToString().Should().Be("http://localhost:8110/sandbox/execute");
    }

    [TestMethod]
    public void BuildEndpointUri_RejectsInvalidScheme()
    {
        Action act = () => NotebookDockerScriptService.BuildEndpointUri("ftp://localhost/sandbox", "execute");

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*must use http or https*");
    }

    [TestMethod]
    public void ResolveScriptExecutionBaseUrl_UsesConfiguredContainerUrl()
    {
        var resolved = NotebookDockerScriptService.ResolveScriptExecutionBaseUrl(
            containerName: "guideants-ai",
            configuredContainerUrl: " http://guideants-ai:80/sandbox ",
            envSuffix: null);

        resolved.Should().Be("http://guideants-ai:80/sandbox");
    }

    [TestMethod]
    public void ResolveScriptExecutionBaseUrl_ThrowsWhenConfiguredContainerUrlMissing()
    {
        Action act = () => NotebookDockerScriptService.ResolveScriptExecutionBaseUrl(
            containerName: "guideants-ai",
            configuredContainerUrl: null,
            envSuffix: null);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ServiceRouting:Containers:guideants-ai:BaseUrl is required*");
    }
}
