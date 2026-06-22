using GuideAntsApi.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Options;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.SystemGuide;
using GuideAntsApi.Tests.TestUtils;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Guides;

internal static class GuidesServiceTestHelper
{
    internal static GuidesService CreateGuidesService(
        ApplicationDbContext context,
        IRuntimeProfileResolver? runtimeProfileResolver = null,
        ISystemGuideCatalogFilter? catalogFilter = null) =>
        new(
            context,
            CreateMarkdownExtractionService(),
            runtimeProfileResolver ?? Mock.Of<IRuntimeProfileResolver>(),
            new StaticOptionsMonitor<SettingsSecretsOptions>(CreateSecretsOptions()),
            catalogFilter ?? EmptySystemGuideCatalogFilter.Instance,
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

    private static SettingsSecretsOptions CreateSecretsOptions() => new()
    {
        ActiveKeyId = "test",
        Keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["test"] = Convert.ToBase64String(new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8,
                9, 10, 11, 12, 13, 14, 15, 16,
                17, 18, 19, 20, 21, 22, 23, 24,
                25, 26, 27, 28, 29, 30, 31, 32
            })
        }
    };
}
