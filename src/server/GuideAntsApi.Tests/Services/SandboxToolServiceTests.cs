using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAntsApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class SandboxToolServiceTests
{
    [TestMethod]
    public async Task ExecuteSandboxToolAsync_Returns_error_when_context_missing()
    {
        var service = new SandboxToolService(
            Mock.Of<INotebookDockerScriptService>(),
            Mock.Of<IServiceProvider>(),
            Mock.Of<IStoragePathResolver>(),
            NullLogger<SandboxToolService>.Instance);

        var result = await service.ExecuteSandboxToolAsync(
            "tool",
            "run",
            new Dictionary<string, object>(),
            "init.py",
            "assistant");

        result.StandardError.Should().Contain("InvocationContext is required");
    }
}
