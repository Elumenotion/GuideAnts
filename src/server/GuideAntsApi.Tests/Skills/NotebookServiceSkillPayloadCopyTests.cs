using System.Reflection;
using System.Text;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Skills;

[TestClass]
public sealed class NotebookServiceSkillPayloadCopyTests
{
    [TestMethod]
    public async Task CopyGuideFilesToNotebookAsync_MaterializesSkillScriptsToResourcesAndOutput()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "guideants_skill_copy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageRoot);

        try
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            await using var context = new ApplicationDbContext(options);

            var projectId = Guid.NewGuid();
            var guideId = Guid.NewGuid();
            context.Projects.Add(new Project { Id = projectId, Title = "Skill Copy Project" });
            context.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Skills Guide",
                Kind = AssistantKind.Guide,
                IsActive = true,
                IsGlobal = true
            });
            context.AssistantFiles.Add(new AssistantFile
            {
                AssistantId = guideId,
                FolderKind = "Skill",
                RelativePath = "Skills/arxiv/scripts/search_arxiv.py",
                ContentBytes = Encoding.UTF8.GetBytes("print('arxiv')")
            });
            await context.SaveChangesAsync();

            var notebookId = Guid.NewGuid();
            context.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "Skill Notebook",
                GuideId = guideId
            });
            await context.SaveChangesAsync();

            var service = CreateNotebookService(context, storageRoot);
            var method = typeof(NotebookService).GetMethod(
                "CopyGuideFilesToNotebookAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("CopyGuideFilesToNotebookAsync not found.");

            await (Task)method.Invoke(service, [context, guideId, projectId, notebookId])!;

            var notebookRoot = Path.Combine(storageRoot, projectId.ToString(), "notebooks", notebookId.ToString());
            var resourcePath = Path.Combine(
                notebookRoot,
                "Resources",
                "Skills",
                "arxiv",
                "scripts",
                "search_arxiv.py");
            File.Exists(resourcePath).Should().BeTrue();
            (await File.ReadAllTextAsync(resourcePath)).Should().Be("print('arxiv')");

            var outputPath = Path.Combine(
                notebookRoot,
                "Output",
                "Skills",
                "arxiv",
                "scripts",
                "search_arxiv.py");

            if (OperatingSystem.IsLinux())
            {
                File.Exists(outputPath).Should().BeTrue();
                (File.GetAttributes(outputPath) & FileAttributes.ReparsePoint).Should().NotBe(0);
            }

            var notebookFiles = await context.NotebookFiles
                .Where(f => f.NotebookId == notebookId)
                .Select(f => f.RelativePath)
                .ToListAsync();
            notebookFiles.Should().Contain("Resources/Skills/arxiv/scripts/search_arxiv.py");
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static NotebookService CreateNotebookService(ApplicationDbContext context, string storageRoot)
    {
        var providerMock = new Mock<IServiceProvider>();
        providerMock.Setup(p => p.GetService(typeof(ApplicationDbContext))).Returns(context);
        providerMock.Setup(p => p.GetService(typeof(GuideAntsApi.Services.SystemGuide.ISystemGuideCatalogFilter)))
            .Returns(GuideAntsApi.Tests.TestUtils.EmptySystemGuideCatalogFilter.Instance);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(providerMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var configurationMock = new Mock<IConfiguration>();
        configurationMock.Setup(c => c["FileStorage:Path"]).Returns(storageRoot);

        var notebookFileServiceMock = new Mock<INotebookFileService>();
        var contentFileServiceMock = new Mock<IContentFileService>();

        return new NotebookService(
            scopeFactoryMock.Object,
            contentFileServiceMock.Object,
            notebookFileServiceMock.Object,
            configurationMock.Object,
            NullLogger<NotebookService>.Instance);
    }
}
