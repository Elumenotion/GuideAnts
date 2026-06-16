using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Services.Search;
using GuideAntsApi.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class MemoryToolsTests
{
    [TestMethod]
    public async Task Search_methods_return_error_for_empty_query_without_provider()
    {
        (await MemoryTools.SearchProjectContent(" ")).Should().Contain("error");
        (await MemoryTools.SearchLocalContent("")).Should().Contain("error");
        (await MemoryTools.WebSearch("")).Should().Contain("error");
        (await MemoryTools.SearchAssistantFiles(" ")).Should().Contain("error");
    }

    [TestMethod]
    public async Task SearchProjectContent_Returns_serialized_results_from_hybrid_searcher()
    {
        var fileId = Guid.NewGuid();
        var searcher = new Mock<IHybridSearcher>();
        searcher.Setup(s => s.SearchAsync(
                "widgets",
                It.IsAny<string>(),
                null,
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HybridSearchResult>
            {
                new()
                {
                    ChunkId = Guid.NewGuid(),
                    ContentFileId = fileId,
                    ChunkIndex = 0,
                    Content = "alpha beta",
                    Score = 0.9
                },
                new()
                {
                    ChunkId = Guid.NewGuid(),
                    ContentFileId = fileId,
                    ChunkIndex = 1,
                    Content = "beta gamma",
                    Score = 0.8
                }
            });

        InitializeMemoryTools(searcher.Object);

        var json = await MemoryTools.SearchProjectContent(
            "widgets",
            context: new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        json.Should().Contain("alpha");
        searcher.Verify(s => s.SearchAsync("widgets", It.IsAny<string>(), null, 5, 0.7, 0.3, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static void InitializeMemoryTools(
        IHybridSearcher hybridSearcher,
        IWebSearchService? webSearchService = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(hybridSearcher);
        services.AddSingleton(webSearchService ?? Mock.Of<IWebSearchService>());

        MemoryTools.InitializeServiceProvider(services.BuildServiceProvider());
    }
}
