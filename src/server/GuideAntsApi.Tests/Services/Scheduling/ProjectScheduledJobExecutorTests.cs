using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Functions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Scheduling;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Scheduling;

[TestClass]
public sealed class ProjectScheduledJobExecutorTests
{
    [TestMethod]
    public async Task ExecuteRunPythonScript_ExitCodeZeroWithStderr_Succeeds()
    {
        var (executor, job) = await CreateExecutorAsync(
            new ScriptExecutionResult
            {
                StandardOutput = "done",
                StandardError = "warning: deprecated API",
                ExitCode = 0
            });

        var result = await executor.ExecuteAsync(job, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.StandardError.Should().Contain("deprecated");
        result.ErrorMessage.Should().BeNull();
    }

    [TestMethod]
    public async Task ExecuteRunPythonScript_NonZeroExitCode_FailsWithRealExitCode()
    {
        var (executor, job) = await CreateExecutorAsync(
            new ScriptExecutionResult
            {
                StandardOutput = string.Empty,
                StandardError = "Traceback...",
                ExitCode = 1
            });

        var result = await executor.ExecuteAsync(job, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        result.ErrorMessage.Should().Contain("exited with code 1");
    }

    [TestMethod]
    public async Task ExecuteRunPythonScript_MissingExitCode_FailsWithoutFabricatingExitCode()
    {
        var (executor, job) = await CreateExecutorAsync(
            new ScriptExecutionResult
            {
                StandardOutput = "partial",
                StandardError = "agent transport error"
            });

        var result = await executor.ExecuteAsync(job, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ExitCode.Should().BeNull();
        result.ErrorMessage.Should().Contain("did not report an exit code");
    }

    private static async Task<(ProjectScheduledJobExecutor Executor, ProjectScheduledJob Job)> CreateExecutorAsync(
        ScriptExecutionResult scriptResult)
    {
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var notebookRoot = Path.Combine(Path.GetTempPath(), $"scheduled-job-exec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notebookRoot);
        var relativePath = "scripts/run.py";
        var scriptPath = Path.Combine(notebookRoot, "scripts");
        Directory.CreateDirectory(scriptPath);
        await File.WriteAllTextAsync(Path.Combine(scriptPath, "run.py"), "print('ok')");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"scheduled-job-exec-{Guid.NewGuid():N}")
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            db.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = $"proj-{Guid.NewGuid():N}" });
            db.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "Notebook",
                Slug = $"nb-{Guid.NewGuid():N}"
            });
            db.NotebookFiles.Add(new NotebookFile
            {
                Id = fileId,
                NotebookId = notebookId,
                RelativePath = relativePath
            });
            await db.SaveChangesAsync();
        }

        var dbFactory = new TestDbContextFactory(options);
        var pathResolver = new Mock<IStoragePathResolver>();
        pathResolver.Setup(r => r.GetNotebookRootPath(projectId, notebookId)).Returns(notebookRoot);

        var docker = new Mock<INotebookDockerScriptService>();
        docker.Setup(d => d.ExecuteDockerScriptAsync(
                It.IsAny<string>(),
                "guideants-ai",
                ScriptType.Python,
                It.IsAny<InvocationContext>()))
            .ReturnsAsync(scriptResult);

        var executor = new ProjectScheduledJobExecutor(
            dbFactory,
            Mock.Of<GuideAntsApi.Services.Conversations.IConversationService>(),
            docker.Object,
            pathResolver.Object,
            NullLogger<ProjectScheduledJobExecutor>.Instance);

        var job = new ProjectScheduledJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            NotebookId = notebookId,
            JobType = ProjectScheduledJobType.RunPythonScript,
            ScriptNotebookFileId = fileId,
            CreatedByUserId = Guid.NewGuid()
        };

        return (executor, job);
    }
}
