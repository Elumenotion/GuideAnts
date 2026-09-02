using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.Configuration;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.Configuration;

[TestClass]
public sealed class StartupPipelineHelpersTests
{
    [TestMethod]
    public void ShouldUseHttpsRedirection_ReturnsFalse_ForHttpOnlyBinding()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:8081"
            })
            .Build();

        StartupPipelineHelpers.ShouldUseHttpsRedirection(configuration).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldUseHttpsRedirection_ReturnsTrue_WhenHttpsBindingPresent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:8081;https://127.0.0.1:8082"
            })
            .Build();

        StartupPipelineHelpers.ShouldUseHttpsRedirection(configuration).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldUseForwardedHeaders_ReturnsTrue_WhenEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:Enabled"] = "true"
            })
            .Build();

        StartupPipelineHelpers.ShouldUseForwardedHeaders(configuration).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldUseForwardedHeaders_ReturnsFalse_ByDefault()
    {
        var configuration = new ConfigurationBuilder().Build();

        StartupPipelineHelpers.ShouldUseForwardedHeaders(configuration).Should().BeFalse();
    }
}
