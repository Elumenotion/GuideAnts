using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuideExportImportServiceSkillsTests
{
    private const string SkillMarkdown = """
---
name: export-skill
description: Export/import round-trip skill.
metadata:
  guideants:
    enabled: true
    display_order: 1
    source: authored
---
# Skill body preserved verbatim
""";

    private const string ReferenceMarkdown = "# Reference content\n\nDetails for the skill.";

    [TestMethod]
    public async Task ExportGuideAsync_WritesSkillFilesAtSkillsPath()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"export-skills-{Guid.NewGuid():N}");
        var guideId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Skills Export Guide",
                Kind = AssistantKind.Guide,
                Description = "desc",
                Instructions = "help",
                Created = DateTime.UtcNow,
                Files =
                [
                    new AssistantFile
                    {
                        FolderKind = "Skill",
                        RelativePath = "Skills/export-skill/SKILL.md",
                        ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown),
                        ContentType = "text/markdown",
                        Created = DateTime.UtcNow
                    },
                    new AssistantFile
                    {
                        FolderKind = "Skill",
                        RelativePath = "Skills/export-skill/references/guide.md",
                        ContentBytes = Encoding.UTF8.GetBytes(ReferenceMarkdown),
                        ContentType = "text/markdown",
                        Created = DateTime.UtcNow
                    }
                ]
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        var bytes = await service.ExportGuideAsync(guideId);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        NormalizeText(ReadZipText(archive, "Skills/export-skill/SKILL.md")).Should().Be(NormalizeText(SkillMarkdown));
        NormalizeText(ReadZipText(archive, "Skills/export-skill/references/guide.md")).Should().Be(NormalizeText(ReferenceMarkdown));
    }

    [TestMethod]
    public async Task ImportGuideAsync_CreatesSkillRowsAtGuideRoot()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-skills-root-{Guid.NewGuid():N}");
        var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(context, new TestDbContextFactory(options), queue, new GuideAntsApi.Services.Guides.Skills.AssistantSkillMetaSync(context));
        var guideName = $"Skill Import Guide {Guid.NewGuid():N}";

        await using var zip = CreateGuideZipWithSkills(guideName, includeNestedAssistantSkills: false);
        var result = await service.ImportGuideAsync(zip);

        result.Success.Should().BeTrue();

        var skillFiles = await context.AssistantFiles
            .Where(f => f.AssistantId == result.GuideId && f.FolderKind == "Skill")
            .ToListAsync();

        skillFiles.Should().HaveCount(2);
        skillFiles.Should().Contain(f =>
            f.RelativePath == "Skills/export-skill/SKILL.md"
            && NormalizeText(f.ContentBytes) == NormalizeText(SkillMarkdown));
        skillFiles.Should().Contain(f =>
            f.RelativePath == "Skills/export-skill/references/guide.md"
            && NormalizeText(f.ContentBytes) == NormalizeText(ReferenceMarkdown));

        queue.Enqueued.Should().BeEmpty();
        (await context.AssistantFileMarkdownShadows.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ImportGuideAsync_CreatesSkillRowsForNestedAssistant()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-skills-nested-{Guid.NewGuid():N}");
        var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(context, new TestDbContextFactory(options), queue, new GuideAntsApi.Services.Guides.Skills.AssistantSkillMetaSync(context));
        var guideName = $"Nested Skill Guide {Guid.NewGuid():N}";
        const string crewName = "Skill Crew";

        await using var zip = CreateGuideZipWithSkills(guideName, includeNestedAssistantSkills: true, crewName);
        var result = await service.ImportGuideAsync(zip);

        result.Success.Should().BeTrue();

        var crewAssistant = await context.Assistants
            .Include(a => a.Files)
            .SingleAsync(a => a.Name == crewName && a.Kind == AssistantKind.Assistant);

        var skillFiles = crewAssistant.Files.Where(f => f.FolderKind == "Skill").ToList();
        skillFiles.Should().HaveCount(2);
        skillFiles.Should().Contain(f => f.RelativePath == "Skills/export-skill/SKILL.md");
        skillFiles.Should().Contain(f => f.RelativePath == "Skills/export-skill/references/guide.md");

        queue.Enqueued.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExportImportRoundTrip_PreservesSkillFilesLosslessly()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"skill-roundtrip-{Guid.NewGuid():N}");
        var guideId = Guid.NewGuid();
        var crewId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            var crew = new Assistant
            {
                Id = crewId,
                Name = "Crew With Skill",
                Kind = AssistantKind.Assistant,
                Description = "crew",
                Instructions = "crew help",
                Created = DateTime.UtcNow,
                Files =
                [
                    new AssistantFile
                    {
                        FolderKind = "Skill",
                        RelativePath = "Skills/export-skill/SKILL.md",
                        ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown),
                        ContentType = "text/markdown",
                        Created = DateTime.UtcNow
                    },
                    new AssistantFile
                    {
                        FolderKind = "Skill",
                        RelativePath = "Skills/export-skill/references/guide.md",
                        ContentBytes = Encoding.UTF8.GetBytes(ReferenceMarkdown),
                        ContentType = "text/markdown",
                        Created = DateTime.UtcNow
                    }
                ]
            };

            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Round Trip Guide",
                Kind = AssistantKind.Guide,
                Description = "desc",
                Instructions = "help",
                Created = DateTime.UtcNow,
                Files =
                [
                    new AssistantFile
                    {
                        FolderKind = "Skill",
                        RelativePath = "Skills/export-skill/SKILL.md",
                        ContentBytes = Encoding.UTF8.GetBytes(SkillMarkdown),
                        ContentType = "text/markdown",
                        Created = DateTime.UtcNow
                    }
                ],
                CrewMembers =
                [
                    new GuideMember
                    {
                        GuideId = guideId,
                        AssistantId = crewId,
                        Assistant = crew,
                        DisplayOrder = 0,
                        Created = DateTime.UtcNow
                    }
                ]
            });
            await seed.SaveChangesAsync();
        }

        await using var exportContext = new ApplicationDbContext(options);
        var exportService = GuidesServiceTestHelper.CreateExportImportService(exportContext, options);
        var zipBytes = await exportService.ExportGuideAsync(guideId);

        var importOptions = BackgroundJobTestHelpers.CreateInMemoryOptions($"skill-roundtrip-import-{Guid.NewGuid():N}");
        await using var importContext = new ApplicationDbContext(importOptions);
        var importService = GuidesServiceTestHelper.CreateExportImportService(importContext, importOptions);
        await using var zipStream = new MemoryStream(zipBytes);
        var importResult = await importService.ImportGuideAsync(zipStream);

        importResult.Success.Should().BeTrue();

        var importedGuide = await importContext.Assistants
            .Include(a => a.Files)
            .Include(a => a.CrewMembers).ThenInclude(m => m.Assistant).ThenInclude(a => a.Files)
            .SingleAsync(a => a.Id == importResult.GuideId);

        var guideSkill = importedGuide.Files.Single(f => f.FolderKind == "Skill");
        guideSkill.RelativePath.Should().Be("Skills/export-skill/SKILL.md");
        NormalizeText(guideSkill.ContentBytes).Should().Be(NormalizeText(SkillMarkdown));

        var crewSkills = importedGuide.CrewMembers.Single().Assistant.Files
            .Where(f => f.FolderKind == "Skill")
            .ToList();
        crewSkills.Should().HaveCount(2);
        NormalizeText(crewSkills.Single(f => f.RelativePath.EndsWith("SKILL.md")).ContentBytes)
            .Should().Be(NormalizeText(SkillMarkdown));
        NormalizeText(crewSkills.Single(f => f.RelativePath.Contains("references/")).ContentBytes)
            .Should().Be(NormalizeText(ReferenceMarkdown));
    }

    [TestMethod]
    public async Task ImportGuideAsync_RejectsMalformedSkillPath()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-skill-bad-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);
        var guideName = $"Bad Skill Guide {Guid.NewGuid():N}";

        await using var zip = CreateGuideZipWithMalformedSkill(guideName);
        var act = async () => await service.ImportGuideAsync(zip);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Skills/<name>/*");
    }

    [TestMethod]
    public async Task BootstrapPptxGuideSkill_SeedsCleanly()
    {
        var contentRoot = ResolveGuideAntsApiContentRoot();
        var bootstrapRoot = Path.Combine(contentRoot, "Resources", "bootstrap", "guides", "pptx-guide");
        Directory.Exists(bootstrapRoot).Should().BeTrue(
            $"bootstrap folder expected at {bootstrapRoot}");

        var skillMarkdownPath = Path.Combine(bootstrapRoot, "Skills", "pptx-author", "SKILL.md");
        var referencePath = Path.Combine(bootstrapRoot, "Skills", "pptx-author", "references", "outline.md");
        File.Exists(skillMarkdownPath).Should().BeTrue();
        File.Exists(referencePath).Should().BeTrue();

        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"bootstrap-skill-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var exportImport = GuidesServiceTestHelper.CreateExportImportService(context, options);
        var environment = new TestWebHostEnvironment(contentRoot);
        var seeder = new RequiredGuidesAssistantsSeeder(
            environment,
            exportImport,
            context,
            NullLogger<RequiredGuidesAssistantsSeeder>.Instance);

        await seeder.SeedAsync();

        var pptxGuide = await context.Assistants
            .Include(a => a.Files)
            .SingleOrDefaultAsync(a => a.Kind == AssistantKind.Guide && a.Name == "PPTX Guide");

        pptxGuide.Should().NotBeNull();
        var skillFiles = pptxGuide!.Files.Where(f => f.FolderKind == "Skill").ToList();
        skillFiles.Should().HaveCount(2);
        skillFiles.Should().Contain(f => f.RelativePath == "Skills/pptx-author/SKILL.md");
        skillFiles.Should().Contain(f => f.RelativePath == "Skills/pptx-author/references/outline.md");
        NormalizeText(skillFiles.Single(f => f.RelativePath.EndsWith("SKILL.md")).ContentBytes)
            .Should().Contain("name: pptx-author");

        var skillFileIds = skillFiles.Select(f => f.Id).ToList();
        var skillShadowCount = await context.AssistantFileMarkdownShadows
            .CountAsync(s => skillFileIds.Contains(s.OriginalAssistantFileId));
        skillShadowCount.Should().Be(0);
    }

    private static MemoryStream CreateGuideZipWithSkills(
        string guideName,
        bool includeNestedAssistantSkills,
        string crewName = "Skill Crew")
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "manifest.json", JsonSerializer.Serialize(new
            {
                name = guideName,
                description = "skill import test",
                tools = Array.Empty<object>(),
                crew = includeNestedAssistantSkills
                    ? new[] { new { name = crewName } }
                    : Array.Empty<object>()
            }));
            WriteTextEntry(archive, "instructions.md", "Guide instructions");
            WriteTextEntry(archive, "Skills/export-skill/SKILL.md", SkillMarkdown);
            WriteTextEntry(archive, "Skills/export-skill/references/guide.md", ReferenceMarkdown);

            if (includeNestedAssistantSkills)
            {
                WriteTextEntry(
                    archive,
                    $"assistants/{crewName}/manifest.json",
                    JsonSerializer.Serialize(new
                    {
                        name = crewName,
                        description = "crew with skill",
                        tools = Array.Empty<object>()
                    }));
                WriteTextEntry(archive, $"assistants/{crewName}/instructions.md", "Crew instructions");
                WriteTextEntry(archive, $"assistants/{crewName}/Skills/export-skill/SKILL.md", SkillMarkdown);
                WriteTextEntry(
                    archive,
                    $"assistants/{crewName}/Skills/export-skill/references/guide.md",
                    ReferenceMarkdown);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateGuideZipWithMalformedSkill(string guideName)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "manifest.json", JsonSerializer.Serialize(new
            {
                name = guideName,
                description = "bad skill path",
                tools = Array.Empty<object>()
            }));
            WriteTextEntry(archive, "instructions.md", "Guide instructions");
            WriteTextEntry(archive, "Skills/only-one-segment.md", "not a valid skill tree");
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string ReadZipText(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        entry.Should().NotBeNull($"zip entry '{path}' should exist");
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    private static string NormalizeText(byte[]? bytes) =>
        NormalizeText(Encoding.UTF8.GetString(bytes ?? []));

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ResolveGuideAntsApiContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "GuideAntsApi", "Resources", "bootstrap", "guides");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "GuideAntsApi");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate GuideAntsApi content root for bootstrap guides.");
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string ApplicationName { get; set; } = "GuideAntsApi.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = Environments.Development;
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = string.Empty;
    }
}
