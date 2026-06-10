using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class QuickStartServiceTests
{
    [TestMethod]
    public async Task CreateQuickStartProjectAsync_Throws_when_creative_guide_template_missing()
    {
        var templateService = new Mock<INotebookTemplateService>();
        templateService.Setup(s => s.GetTemplatesAsync(It.IsAny<Guid>())).ReturnsAsync([]);

        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"quick-start-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);

        var service = new QuickStartService(
            Mock.Of<IProjectService>(),
            Mock.Of<INotebookService>(),
            Mock.Of<IConversationService>(),
            templateService.Object,
            context);

        var act = async () => await service.CreateQuickStartProjectAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Creative Guide template not found*");
    }
}
