using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;
using GuideAnts.Usage;
using GuideAntsApi.Tests.BackgroundJobs;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class PublishedConversationServiceTests
{
    [TestMethod]
    public async Task CreateConversationAsync_Throws_when_notebook_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-missing-{Guid.NewGuid():N}");
        var provider = CreateServiceProvider(options);

        var service = CreateService(provider);

        var act = async () => await service.CreateConversationAsync(Guid.NewGuid(), "Test");

        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("*Notebook not found*");
    }

    [TestMethod]
    public async Task CreateConversationAsync_Creates_conversation_for_existing_notebook()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-create-{Guid.NewGuid():N}");
        var notebookId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            var (projectId, nbId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            notebookId = nbId;
        }

        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        var created = await service.CreateConversationAsync(notebookId, "Published chat");

        created.Title.Should().Be("Published chat");
        await using var verify = new ApplicationDbContext(options);
        verify.NotebookConversations.Should().ContainSingle(c => c.Id == created.Id);
    }

    private static ServiceProvider CreateServiceProvider(DbContextOptions<ApplicationDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestDbContextFactory(options));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(sp => sp.GetRequiredService<TestDbContextFactory>());
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        return services.BuildServiceProvider();
    }

    private static PublishedConversationService CreateService(IServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<IContextOptionsService>(),
            Mock.Of<IChatCompletionClientFactory>(),
            Mock.Of<IUsageRecorder>(),
            NullLogger<PublishedConversationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new MarkdownAttachmentOptions()),
            Mock.Of<IChatModelResolver>());
}
