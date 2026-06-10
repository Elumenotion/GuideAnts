using FluentAssertions;
using GuideAntsApi.Services;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class LegacyStoragePathResolverTests
{
    [TestMethod]
    public void Resolves_guid_based_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storage-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        const string hash = "abcdef1234567890abcdef1234567890abcdef12";
        var resolver = new LegacyStoragePathResolver(root);

        resolver.GetStorageRoot().Should().Be(root);
        resolver.GetProjectRootPath(projectId).Should().Be(Path.Combine(root, projectId.ToString()));
        resolver.GetNotebookRootPath(projectId, notebookId).Should()
            .Be(Path.Combine(root, projectId.ToString(), "notebooks", notebookId.ToString()));
        resolver.GetContainerNotebookRootPath(projectId, notebookId).Should()
            .Be($"/app/ContentFiles/{projectId}/notebooks/{notebookId}");
        resolver.GetContentAddressablePath(projectId, hash).Should().EndWith(hash);
        resolver.GetProjectMarkdownShadowPath(projectId, hash).Should().EndWith($"{hash}.md");
        resolver.GetNotebookMarkdownShadowPath(projectId, notebookId, hash).Should().EndWith($"{hash}.md");
    }
}
