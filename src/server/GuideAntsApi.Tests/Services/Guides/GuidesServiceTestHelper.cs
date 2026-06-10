using GuideAntsApi.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Options;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Guides;

internal static class GuidesServiceTestHelper
{
    internal static GuidesService CreateGuidesService(ApplicationDbContext context) =>
        new(
            context,
            CreateMarkdownExtractionService(),
            Mock.Of<IRuntimeProfileResolver>(),
            NullLogger<GuidesService>.Instance);

    internal static GuideExportImportService CreateExportImportService(ApplicationDbContext context, DbContextOptions<ApplicationDbContext> options) =>
        new(context, new TestDbContextFactory(options), Mock.Of<IJobQueueService>());

    internal static GuideUsageService CreateGuideUsageService(
        ApplicationDbContext context,
        DbContextOptions<ApplicationDbContext> options) =>
        new(context, new TestDbContextFactory(options), NullLogger<GuideUsageService>.Instance);

    private static MarkdownExtractionService CreateMarkdownExtractionService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Path"] = Path.GetTempPath() })
            .Build();

        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(Mock.Of<IServiceProvider>());
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new MarkdownExtractionService(
            scopeFactory.Object,
            Mock.Of<IJobQueueService>(),
            Microsoft.Extensions.Options.Options.Create(new MarkdownExtractionOptions()),
            configuration,
            NullLogger<MarkdownExtractionService>.Instance);
    }
}
