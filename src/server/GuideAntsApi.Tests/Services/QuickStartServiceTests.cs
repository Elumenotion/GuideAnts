using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Core;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class QuickStartServiceTests
{
    [TestMethod]
    public async Task CreateQuickStartProjectAsync_Throws_when_creative_guide_template_missing()
    {
        var projectId = Guid.NewGuid();
        var projectService = new Mock<IProjectService>();
        projectService
            .Setup(s => s.CreateProjectAsync(It.IsAny<CreateProjectDto>()))
            .ReturnsAsync(new ProjectDto(
                projectId,
                "Quick Start",
                string.Empty,
                DateTime.UtcNow,
                null,
                true));

        var templateService = new Mock<INotebookTemplateService>();
        templateService.Setup(s => s.GetTemplatesAsync(projectId, It.IsAny<Guid?>())).ReturnsAsync([]);

        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"quick-start-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);

        var service = new QuickStartService(
            projectService.Object,
            Mock.Of<INotebookService>(),
            Mock.Of<IConversationService>(),
            templateService.Object,
            context);

        var act = async () => await service.CreateQuickStartProjectAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Creative Guide template not found*");
    }
}
