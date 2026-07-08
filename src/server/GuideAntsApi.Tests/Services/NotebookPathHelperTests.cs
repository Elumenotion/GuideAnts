using System.Reflection;
using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAntsApi.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class NotebookPathHelperTests
{
    private static readonly Type HelperType = typeof(NotebookDockerScriptService).Assembly
        .GetType("GuideAntsApi.Services.NotebookPathHelper", throwOnError: true)!;

    [TestCleanup]
    public void Cleanup()
    {
        // Reset static provider to avoid cross-test leakage.
        InitializeServiceProvider(new ServiceCollection().BuildServiceProvider());
    }

    [TestMethod]
    public void GetGeneratedImageDbRelativePath_PrivateNotebook_UsesOutputFolder()
    {
        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            IsPublished = false
        };

        NotebookPathHelper.GetGeneratedImageDbRelativePath(context, "wire-abc.png")
            .Should().Be("Output/wire-abc.png");
    }

    [TestMethod]
    public void GetGeneratedImageDbRelativePath_PublishedNotebook_UsesRunsFolder()
    {
        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            IsPublished = true,
            RunId = "Ab12Cd34Ef"
        };

        NotebookPathHelper.GetGeneratedImageDbRelativePath(context, "wire-abc.png")
            .Should().Be("Runs/Ab12Cd34Ef/wire-abc.png");
    }

    [TestMethod]
    public void GetWorkingDirectory_WithResolver_EnsuresMetadataBeforeContainerPath()
    {
        var resolver = new RecordingStoragePathResolver(
            notebookRootPath: "/tmp/notebook-root",
            containerNotebookRootPath: "/app/ContentFiles/project-slug/notebook-slug");
        var services = new ServiceCollection();
        services.AddSingleton<IStoragePathResolver>(resolver);
        InitializeServiceProvider(services.BuildServiceProvider());

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) { IsPublished = false };

        var workingDirectory = GetWorkingDirectory(context);

        workingDirectory.Should().Be("/app/ContentFiles/project-slug/notebook-slug/Output");
        resolver.Calls.Should().ContainInOrder("GetNotebookRootPath", "GetContainerNotebookRootPath");
    }

    [TestMethod]
    public void GetWorkingDirectory_WithoutResolver_UsesLegacyFallbackPath()
    {
        InitializeServiceProvider(new ServiceCollection().BuildServiceProvider());
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var context = new InvocationContext(projectId, notebookId, Guid.NewGuid()) { IsPublished = false };

        var workingDirectory = GetWorkingDirectory(context);

        workingDirectory.Should().Be($"/app/ContentFiles/{projectId}/notebooks/{notebookId}/Output");
    }

    private static void InitializeServiceProvider(IServiceProvider provider)
    {
        var method = HelperType.GetMethod("InitializeServiceProvider", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("NotebookPathHelper.InitializeServiceProvider not found.");
        method.Invoke(null, [provider]);
    }

    private static string GetWorkingDirectory(InvocationContext context)
    {
        var method = HelperType.GetMethod("GetWorkingDirectory", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("NotebookPathHelper.GetWorkingDirectory not found.");
        return (string)(method.Invoke(null, [context])
            ?? throw new InvalidOperationException("GetWorkingDirectory returned null."));
    }

    private sealed class RecordingStoragePathResolver(string notebookRootPath, string containerNotebookRootPath)
        : IStoragePathResolver
    {
        public List<string> Calls { get; } = [];

        public string GetStorageRoot() => "/tmp/storage-root";

        public string GetProjectRootPath(Guid projectId) => "/tmp/project-root";

        public string GetNotebookRootPath(Guid projectId, Guid notebookId)
        {
            Calls.Add(nameof(GetNotebookRootPath));
            return notebookRootPath;
        }

        public string GetContainerNotebookRootPath(Guid projectId, Guid notebookId)
        {
            Calls.Add(nameof(GetContainerNotebookRootPath));
            return containerNotebookRootPath;
        }

        public string GetContentAddressablePath(Guid projectId, string contentHash) => string.Empty;

        public string GetProjectMarkdownShadowPath(Guid projectId, string contentHash) => string.Empty;

        public string GetNotebookMarkdownShadowPath(Guid projectId, Guid notebookId, string contentHash) => string.Empty;

        public void InvalidateProject(Guid projectId)
        {
        }

        public void InvalidateNotebook(Guid notebookId)
        {
        }
    }
}
