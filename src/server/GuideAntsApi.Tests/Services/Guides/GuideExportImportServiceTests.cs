using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuideExportImportServiceTests
{
    [TestMethod]
    public async Task ExportGuideAsync_Throws_when_guide_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"export-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        var act = async () => await service.ExportGuideAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Guide not found*");
    }

    [TestMethod]
    public async Task PreviewImportAsync_Throws_for_empty_stream()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-preview-empty-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        await using var emptyZip = new MemoryStream();
        var act = async () => await service.PreviewImportAsync(emptyZip);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [TestMethod]
    public async Task ExportGuideAsync_Returns_zip_for_existing_guide()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"export-guide-{Guid.NewGuid():N}");
        var guideId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Exportable Guide",
                Kind = AssistantKind.Guide,
                Description = "desc",
                Instructions = "help",
                ModelId = "gpt-4.1",
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        var bytes = await service.ExportGuideAsync(guideId);

        bytes.Should().NotBeNull();
        bytes!.Length.Should().BeGreaterThan(0);
    }
}
