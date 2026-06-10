using System.Net;
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

[TestClass]
public sealed class NotebookImageServiceDeepTests
{
    // ----- GenerateImageAsync validation branches (return before storage resolution) -----

    [TestMethod]
    public async Task GenerateImageAsync_Returns_error_for_empty_prompt()
    {
        var service = CreateService();

        var result = await service.GenerateImageAsync("   ", "image.png",
            context: new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        result.StandardError.Should().Contain("Prompt cannot be null or empty.");
        result.NewFiles.Should().BeNull();
    }

    [TestMethod]
    public async Task GenerateImageAsync_Returns_error_for_empty_filename()
    {
        var service = CreateService();

        var result = await service.GenerateImageAsync("a prompt", "  ",
            context: new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        result.StandardError.Should().Contain("Filename is required.");
    }

    [TestMethod]
    public async Task GenerateImageAsync_Returns_error_when_context_missing()
    {
        var service = CreateService();

        var result = await service.GenerateImageAsync("a prompt", "image.png", context: null);

        result.StandardError.Should().Contain("Project ID and Notebook ID are required.");
    }

    // ----- CreateImageFromImageAsync validation branches -----

    [TestMethod]
    public async Task CreateImageFromImageAsync_Returns_error_for_empty_prompt()
    {
        var service = CreateService();

        var result = await service.CreateImageFromImageAsync(" ", "src.png", "out.png",
            context: new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        result.StandardError.Should().Contain("Prompt cannot be null or empty.");
    }

    [TestMethod]
    public async Task CreateImageFromImageAsync_Returns_error_for_empty_source_filename()
    {
        var service = CreateService();

        var result = await service.CreateImageFromImageAsync("a prompt", " ", "out.png",
            context: new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        result.StandardError.Should().Contain("Source image filename is required.");
    }

    [TestMethod]
    public async Task CreateImageFromImageAsync_Returns_error_for_empty_output_filename()
    {
        var service = CreateService();

        var result = await service.CreateImageFromImageAsync("a prompt", "src.png", " ",
            context: new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        result.StandardError.Should().Contain("Output filename is required.");
    }

    [TestMethod]
    public async Task CreateImageFromImageAsync_Returns_error_when_context_missing()
    {
        var service = CreateService();

        var result = await service.CreateImageFromImageAsync("a prompt", "src.png", "out.png", context: null);

        result.StandardError.Should().Contain("Project ID and Notebook ID are required.");
    }

    [TestMethod]
    public async Task CreateImageFromImageAsync_Returns_error_when_source_file_missing()
    {
        var notebookId = Guid.NewGuid();
        var provider = BuildServiceProvider(notebookId, seedFileRelativePath: null);
        var service = CreateService(serviceProvider: provider);

        var result = await service.CreateImageFromImageAsync(
            "a prompt",
            "missing.png",
            "out.png",
            context: new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid()));

        result.StandardError.Should().Contain("Source image file not found in notebook");
    }

    [TestMethod]
    public async Task CreateImageFromImageAsync_Returns_error_when_source_file_not_image()
    {
        var notebookId = Guid.NewGuid();
        var provider = BuildServiceProvider(notebookId, seedFileRelativePath: "notes.txt");
        var service = CreateService(serviceProvider: provider);

        var result = await service.CreateImageFromImageAsync(
            "a prompt",
            "notes.txt",
            "out.png",
            context: new InvocationContext(Guid.NewGuid(), notebookId, Guid.NewGuid()));

        result.StandardError.Should().Contain("not a supported image format");
    }

    // ----- helpers -----

    private static NotebookImageService CreateService(IServiceProvider? serviceProvider = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var handler = new StubHandler();
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var resolver = new FakeServiceModeResolver(
            RoutedServiceNames.ImageGeneration,
            providerSection: "AzureOpenAiImages");

        return new NotebookImageService(
            httpClientFactory.Object,
            configuration,
            NullLogger<NotebookImageService>.Instance,
            serviceProvider: serviceProvider!,
            serviceModeResolver: resolver);
    }

    private static IServiceProvider BuildServiceProvider(Guid notebookId, string? seedFileRelativePath)
    {
        // Provide FileStorage:Path through the scoped IConfiguration so the service
        // resolves a storage root without relying on shared OS environment variables.
        var storageRoot = Path.Combine(Path.GetTempPath(), "guideants-image-deep", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageRoot);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Path"] = storageRoot
            })
            .Build();

        var dbName = $"image-deep-{Guid.NewGuid():N}";
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddDbContext<ApplicationDbContext>(options =>
                options.UseInMemoryDatabase(dbName))
            .AddSingleton(Mock.Of<INotebookFileService>())
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

        return services;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
