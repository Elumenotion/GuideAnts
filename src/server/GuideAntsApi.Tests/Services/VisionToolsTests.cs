using AntRunner.ToolCalling;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Tests.Services;

[TestClass]
[DoNotParallelize] // VisionTools holds a process-global service provider
public sealed class VisionToolsTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid NotebookId = Guid.NewGuid();

    private readonly string _notebookRoot =
        Path.Combine(Path.GetTempPath(), $"vision-tools-{Guid.NewGuid():N}");

    /// <summary>Minimal PNG magic bytes; the tool sniffs the header, not the extension.</summary>
    private static byte[] Png(int padding = 4) =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(new byte[padding]).ToArray();

    private sealed class StubResolver : IStoragePathResolver
    {
        private readonly string _root;
        public StubResolver(string root) => _root = root;
        public string GetStorageRoot() => _root;
        public string GetProjectRootPath(Guid projectId) => Path.Combine(_root, "..", projectId.ToString());
        public string GetNotebookRootPath(Guid projectId, Guid notebookId) => _root;
        public string GetContainerNotebookRootPath(Guid projectId, Guid notebookId) => _root;
        public string GetContentAddressablePath(Guid projectId, string contentHash) => Path.Combine(_root, "cas", contentHash);
        public string GetProjectMarkdownShadowPath(Guid projectId, string contentHash) => Path.Combine(_root, "shadow", contentHash);
        public string GetNotebookMarkdownShadowPath(Guid projectId, Guid notebookId, string contentHash) => Path.Combine(_root, "shadow", contentHash);
        public void InvalidateProject(Guid projectId) { }
        public void InvalidateNotebook(Guid notebookId) { }
    }

    private void Init()
    {
        Directory.CreateDirectory(_notebookRoot);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStoragePathResolver>(new StubResolver(_notebookRoot));
        VisionTools.InitializeServiceProvider(services.BuildServiceProvider());
    }

    private string Write(string relativePath, byte[] bytes)
    {
        var full = Path.Combine(_notebookRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private static InvocationContext Context() => new(
        ProjectId,
        NotebookId,
        Guid.NewGuid(),
        OAuthUserAccessToken: null,
        ExternalAuthTokens: null);

    public void Dispose()
    {
        try { Directory.Delete(_notebookRoot, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task ImageOnDisk_ReturnsVisionToolResultWithDataUrl()
    {
        Init();
        var bytes = Png();
        Write("data/plot.png", bytes);

        var result = await VisionTools.GetImageForInspection("data/plot.png", "checking the plot", Context());

        result.Output.Should().Contain("data/plot.png");
        result.Output.Should().NotContain(Convert.ToBase64String(bytes)); // bytes only in the attachment
        result.Attachments.Should().ContainSingle();
        var att = result.Attachments[0];
        att.DataUrl.Should().StartWith("data:image/png;base64,");
        att.SourcePath.Should().Be("data/plot.png");
        att.SourceTool.Should().Be("GetImageForInspection");
        att.Caption.Should().Be("checking the plot");
    }

    [TestMethod]
    public async Task NewlyWrittenFile_IsReadWithoutAnyIndexRow()
    {
        // Regression: the tool used to resolve paths through the NotebookFiles table, so a file
        // created by run_python earlier in the same turn reported "No file found".
        Init();
        var bytes = Png();
        Write("Output/random-number-test.png", bytes);

        var result = await VisionTools.GetImageForInspection("random-number-test.png", null, Context());

        result.Output.Should().NotContain("error");
        result.Attachments.Should().ContainSingle().Which
            .DataUrl.Should().StartWith("data:image/png;base64,");
    }

    [TestMethod]
    public async Task MissingPath_ReturnsErrorWithNoAttachments()
    {
        Init();

        var result = await VisionTools.GetImageForInspection("data/nope.png", null, Context());

        result.Output.Should().Contain("No image file found");
        result.Attachments.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TextFileWithImageExtension_IsRejected()
    {
        Init();
        Write("notes.png", System.Text.Encoding.UTF8.GetBytes("not actually a png"));

        var result = await VisionTools.GetImageForInspection("notes.png", null, Context());

        result.Output.Should().Contain("not a supported image");
        result.Attachments.Should().BeEmpty();
    }

    [TestMethod]
    public async Task OversizeFile_ReturnsError()
    {
        Init();
        Write("big.png", Png(padding: (int)VisionTools.MaxImageBytes));

        var result = await VisionTools.GetImageForInspection("big.png", null, Context());

        result.Output.Should().Contain("maximum");
        result.Attachments.Should().BeEmpty();
    }

    [TestMethod]
    public async Task EmptyPath_ReturnsError()
    {
        Init();

        var result = await VisionTools.GetImageForInspection("  ", null, Context());

        result.Output.Should().Contain("path is required");
        result.Attachments.Should().BeEmpty();
    }

    [TestMethod]
    public async Task BackslashPath_NormalizesToForwardSlash()
    {
        Init();
        Write("data/plot.png", Png());

        var result = await VisionTools.GetImageForInspection("data\\plot.png", null, Context());

        result.Attachments.Should().ContainSingle().Which.SourcePath.Should().Be("data/plot.png");
    }

    [TestMethod]
    public async Task TraversalPath_IsRejected()
    {
        Init();
        Write("data/plot.png", Png());

        var result = await VisionTools.GetImageForInspection("../outside.png", null, Context());

        result.Output.Should().Contain("No image file found");
        result.Attachments.Should().BeEmpty();
    }
}
