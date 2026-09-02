using System.Text.Json;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Functions;
using FluentAssertions;
using GuideAntsApi.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class SandboxToolServiceTests
{
    [TestMethod]
    public async Task ExecuteSandboxToolAsync_Returns_error_when_context_missing()
    {
        var service = CreateService(
            Mock.Of<INotebookDockerScriptService>(),
            Mock.Of<IStoragePathResolver>());

        var result = await service.ExecuteSandboxToolAsync(
            "tool",
            "run",
            new Dictionary<string, object>(),
            "init.py",
            "assistant");

        result.StandardError.Should().Contain("InvocationContext is required");
    }

    [TestMethod]
    public async Task ExecuteSandboxToolAsync_Returns_error_when_module_missing()
    {
        var pathResolver = new Mock<IStoragePathResolver>();
        pathResolver.Setup(r => r.GetNotebookRootPath(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(Path.Combine(Path.GetTempPath(), $"sandbox-missing-{Guid.NewGuid():N}"));
        pathResolver.Setup(r => r.GetContainerNotebookRootPath(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns("/app/ContentFiles/project/notebook");

        var service = CreateService(Mock.Of<INotebookDockerScriptService>(), pathResolver.Object);
        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result = await service.ExecuteSandboxToolAsync(
            "tool",
            "run",
            new Dictionary<string, object>(),
            "init.py",
            "Crew Assistant",
            context);

        result.StandardError.Should().Contain("Module containing");
    }

    [TestMethod]
    public async Task ExecuteSandboxToolAsync_Executes_generated_script_when_module_exists()
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var notebookRoot = Path.Combine(Path.GetTempPath(), $"sandbox-exec-{Guid.NewGuid():N}");
        var resourcesDir = Path.Combine(notebookRoot, "Resources", "crew-Crew-Assistant");
        Directory.CreateDirectory(resourcesDir);
        await File.WriteAllTextAsync(Path.Combine(resourcesDir, "init.py"), "# module");

        var pathResolver = new Mock<IStoragePathResolver>();
        pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookId)).Returns(notebookRoot);
        pathResolver.Setup(r => r.GetContainerNotebookRootPath(projectId, notebookId))
            .Returns("/app/ContentFiles/project/notebook");

        var docker = new Mock<INotebookDockerScriptService>();
        docker.Setup(d => d.ExecuteDockerScriptAsync(
                It.Is<string>(s => s.Contains("importlib") && s.Contains("run")),
                "guideants-ai",
                ScriptType.Python,
                It.IsAny<InvocationContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { StandardOutput = "{\"ok\":true}" });

        var service = CreateService(docker.Object, pathResolver.Object);
        var context = new InvocationContext(projectId, notebookId, Guid.NewGuid());
        var parameters = new Dictionary<string, object>
        {
            ["value"] = JsonSerializer.SerializeToElement("test")
        };

        var result = await service.ExecuteSandboxToolAsync(
            "tool",
            "run",
            parameters,
            "init.py",
            "Crew Assistant",
            context);

        result.StandardOutput.Should().Contain("ok");
        docker.VerifyAll();

        try { Directory.Delete(notebookRoot, recursive: true); } catch { /* best effort */ }
    }

    private static SandboxToolService CreateService(
        INotebookDockerScriptService docker,
        IStoragePathResolver pathResolver)
    {
        var services = new ServiceCollection();
        services.AddSingleton(docker);
        services.AddSingleton(pathResolver);
        var provider = services.BuildServiceProvider();
        return new SandboxToolService(
            docker,
            provider,
            pathResolver,
            NullLogger<SandboxToolService>.Instance);
    }
}
