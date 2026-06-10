using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

/// <summary>
/// Additional deterministic coverage for <see cref="NotebookImageService"/> that does NOT rely on
/// shared OS environment variables. These cases drive the source-image loading guards in
/// <see cref="NotebookImageService.CreateImageFromImageAsync"/> and the routing-exception path
/// (all of which return/throw before the trailing DOCKER_EXEC_LOG_PATH log write). The HTTP
/// provider success paths that require DOCKER_EXEC_LOG_PATH mutation are intentionally left to
/// the existing <c>NotebookImageServiceTests</c> suite ([DoNotParallelize] + env scope).
/// </summary>
[TestClass]
public sealed class NotebookImageServiceProviderTests
{
    [TestMethod]
    public async Task CreateImageFromImageAsync_Returns_error_when_file_content_cannot_be_loaded()
    {
        var notebookId = Guid.NewGuid();
        // Default INotebookFileService returns null for GetFileContentStreamAsync(id).
        var (provider, _) = BuildServiceProvider(notebookId, "pic.png", fileService: null);
        var service = CreateService(provider, "AzureOpenAiImages");

        var result = await service.CreateImageFromImageAsync(
            "a prompt", "pic.png", "out.png",
            context: new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid()));

        result.StandardError.Should().Contain("Failed to load source image file content");
    }

    [TestMethod]
    public async Task CreateImageFromImageAsync_Returns_error_when_source_bytes_empty()
    {
        var notebookId = Guid.NewGuid();
        var fileService = new Mock<INotebookFileService>();
        fileService
            .Setup(f => f.GetFileContentStreamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, CancellationToken _) =>
                ((Stream Stream, string ContentType, string FileName)?)
                (new MemoryStream(Array.Empty<byte>()), "image/png", "pic.png"));

        var (provider, _) = BuildServiceProvider(notebookId, "pic.png", fileService.Object);
        var service = CreateService(provider, "AzureOpenAiImages");

        var result = await service.CreateImageFromImageAsync(
            "a prompt", "pic.png", "out.png",
            context: new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid()));

        result.StandardError.Should().Contain("Failed to load source image bytes");
    }

    [TestMethod]
    public async Task CreateImageFromImageAsync_ThrowsRoutingException_ForUnsupportedProviderSection()
    {
        var notebookId = Guid.NewGuid();
        var pngBytes = MinimalPng();
        var fileService = new Mock<INotebookFileService>();
        fileService
            .Setup(f => f.GetFileContentStreamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, CancellationToken _) =>
                ((Stream Stream, string ContentType, string FileName)?)
                (new MemoryStream(pngBytes), "image/png", "pic.png"));

        var (provider, _) = BuildServiceProvider(notebookId, "pic.png", fileService.Object);
        var service = CreateService(provider, "TotallyBogusSection");

        var act = async () => await service.CreateImageFromImageAsync(
            "a prompt", "pic.png", "out.png",
            context: new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid()));

        var ex = await act.Should().ThrowAsync<RoutingException>();
        ex.Which.Code.Should().Be(RoutingErrorCodes.ProviderNotReady);
    }

    [TestMethod]
    public async Task GenerateImageAsync_ThrowsRoutingException_ForUnsupportedProviderSection_WithScopedConfig()
    {
        var notebookId = Guid.NewGuid();
        var (provider, _) = BuildServiceProvider(notebookId, seedFileRelativePath: null, fileService: null);
        var service = CreateService(provider, "TotallyBogusSection");

        var act = async () => await service.GenerateImageAsync(
            "a prompt", "out.png",
            context: new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid()));

        var ex = await act.Should().ThrowAsync<RoutingException>();
        ex.Which.Code.Should().Be(RoutingErrorCodes.ProviderNotReady);
        ex.Which.ProviderSection.Should().Be("TotallyBogusSection");
    }

    private static NotebookImageService CreateService(IServiceProvider serviceProvider, string providerSection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(new StubHandler()));

        var resolver = new FakeServiceModeResolver(RoutedServiceNames.ImageGeneration, providerSection: providerSection);

        return new NotebookImageService(
            httpClientFactory.Object,
            configuration,
            NullLogger<NotebookImageService>.Instance,
            serviceProvider: serviceProvider,
            serviceModeResolver: resolver);
    }

    private static (IServiceProvider Provider, string StorageRoot) BuildServiceProvider(
        Guid notebookId,
        string? seedFileRelativePath,
        INotebookFileService? fileService)
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "guideants-image-provider", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageRoot);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Path"] = storageRoot })
            .Build();

        var dbName = $"image-provider-{Guid.NewGuid():N}";
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName))
            .AddSingleton(fileService ?? Mock.Of<INotebookFileService>())
            .BuildServiceProvider();

        if (seedFileRelativePath != null)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookFiles.Add(new NotebookFile
            {
                Id = Guid.NewGuid(),
                NotebookId = notebookId,
                RelativePath = seedFileRelativePath,
                FileHash = "hash",
                LastModifiedUtc = DateTime.UtcNow,
                Created = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        return (services, storageRoot);
    }

    private static byte[] MinimalPng()
    {
        // PNG signature + IHDR with a 64x64 size so GetImageDimensions parses cleanly.
        var bytes = new byte[26];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
        // width = 64 at offset 16-19, height = 64 at offset 20-23
        bytes[19] = 0x40;
        bytes[23] = 0x40;
        return bytes;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
