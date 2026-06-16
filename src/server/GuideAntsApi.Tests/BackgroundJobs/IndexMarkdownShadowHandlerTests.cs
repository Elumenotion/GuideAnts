using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Services.Indexing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class IndexMarkdownShadowHandlerTests
{
    [TestMethod]
    public async Task IndexNotebookMarkdownShadowHandler_Skips_when_shadow_not_ready()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"index-nb-shadow-{Guid.NewGuid():N}");
        var indexer = new Mock<IHybridIndexer>();
        var handler = new IndexNotebookMarkdownShadowHandler(
            NullLogger<IndexNotebookMarkdownShadowHandler>.Instance,
            BackgroundJobTestHelpers.CreateFactory(options),
            indexer.Object,
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));

        var success = await handler.HandleAsync(new IndexNotebookMarkdownShadowJob(Guid.NewGuid()), CancellationToken.None);

        success.Should().BeTrue();
        indexer.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task IndexContentMarkdownShadowHandler_Skips_when_shadow_not_ready()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"index-content-shadow-{Guid.NewGuid():N}");
        var indexer = new Mock<IHybridIndexer>();
        var handler = new IndexContentMarkdownShadowHandler(
            NullLogger<IndexContentMarkdownShadowHandler>.Instance,
            BackgroundJobTestHelpers.CreateFactory(options),
            indexer.Object,
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));

        var success = await handler.HandleAsync(new IndexContentMarkdownShadowJob(Guid.NewGuid()), CancellationToken.None);

        success.Should().BeTrue();
        indexer.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task IndexAssistantFileMarkdownShadowHandler_Skips_when_shadow_not_ready()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"index-assistant-shadow-{Guid.NewGuid():N}");
        var indexer = new Mock<IHybridIndexer>();
        var handler = new IndexAssistantFileMarkdownShadowHandler(
            NullLogger<IndexAssistantFileMarkdownShadowHandler>.Instance,
            BackgroundJobTestHelpers.CreateFactory(options),
            indexer.Object,
            BackgroundJobTestHelpers.CreateConfiguration(Path.GetTempPath()));

        var success = await handler.HandleAsync(new IndexAssistantFileMarkdownShadowJob(Guid.NewGuid()), CancellationToken.None);

        success.Should().BeTrue();
        indexer.VerifyNoOtherCalls();
    }
}
