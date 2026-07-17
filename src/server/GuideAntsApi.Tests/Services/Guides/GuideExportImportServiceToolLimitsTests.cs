using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuideExportImportServiceToolLimitsTests
{
    [TestMethod]
    public async Task ExportImportRoundTrip_PreservesToolLimitsOnGuideAndCrewAssistant()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-limits-roundtrip-{Guid.NewGuid():N}");
        var guideId = Guid.NewGuid();
        var crewId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            var crew = new Assistant
            {
                Id = crewId,
                Name = "Limited Crew Assistant",
                Kind = AssistantKind.Assistant,
                Description = "crew",
                Instructions = "crew help",
                MaxToolCallsPerTurn = 8,
                Created = DateTime.UtcNow,
            };

            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Limited Guide",
                Kind = AssistantKind.Guide,
                Description = "desc",
                Instructions = "help",
                MaxToolCallsPerTurn = 15,
                Created = DateTime.UtcNow,
                CrewMembers =
                [
                    new GuideMember
                    {
                        GuideId = guideId,
                        AssistantId = crewId,
                        Assistant = crew,
                        DisplayOrder = 0,
                        MaxToolCallsPerInvocation = 5,
                        Created = DateTime.UtcNow,
                    }
                ],
            });
            await seed.SaveChangesAsync();
        }

        await using var exportContext = new ApplicationDbContext(options);
        var exportService = GuidesServiceTestHelper.CreateExportImportService(exportContext, options);
        var zipBytes = await exportService.ExportGuideAsync(guideId);

        await using var zipStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        using var guideManifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        using var guideManifest = JsonDocument.Parse(await guideManifestReader.ReadToEndAsync());
        guideManifest.RootElement.GetProperty("max_tool_calls_per_turn").GetInt32().Should().Be(15);
        guideManifest.RootElement.GetProperty("crew")[0]
            .GetProperty("max_tool_calls_per_invocation").GetInt32().Should().Be(5);

        using var crewManifestReader = new StreamReader(
            archive.GetEntry("assistants/Limited Crew Assistant/manifest.json")!.Open());
        using var crewManifest = JsonDocument.Parse(await crewManifestReader.ReadToEndAsync());
        crewManifest.RootElement.GetProperty("max_tool_calls_per_turn").GetInt32().Should().Be(8);

        var importOptions = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-limits-import-{Guid.NewGuid():N}");
        await using var importContext = new ApplicationDbContext(importOptions);
        var importService = GuidesServiceTestHelper.CreateExportImportService(importContext, importOptions);
        zipStream.Position = 0;
        var importResult = await importService.ImportGuideAsync(zipStream);
        importResult.Success.Should().BeTrue();

        var importedGuide = await importContext.Assistants
            .Include(a => a.CrewMembers)
            .SingleAsync(a => a.Id == importResult.GuideId);

        importedGuide.MaxToolCallsPerTurn.Should().Be(15);
        importedGuide.CrewMembers.Single().MaxToolCallsPerInvocation.Should().Be(5);

        var importedCrew = await importContext.Assistants
            .SingleAsync(a => a.Name == "Limited Crew Assistant" && a.Kind == AssistantKind.Assistant);
        importedCrew.MaxToolCallsPerTurn.Should().Be(8);
    }

    [TestMethod]
    public async Task ImportAssistant_FromManifest_PreservesCreativeSearchBootstrapLimit()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"search-bootstrap-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        const string manifestJson = """
            {
              "name": "Search",
              "description": "search assistant",
              "max_tool_calls_per_turn": 12
            }
            """;

        await using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("manifest.json");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(manifestJson);
        }

        zipStream.Position = 0;
        var result = await service.ImportAssistantAsync(zipStream);
        result.Success.Should().BeTrue();

        var assistant = await context.Assistants.SingleAsync(a => a.Id == result.GuideId);
        assistant.MaxToolCallsPerTurn.Should().Be(12);
    }
}
