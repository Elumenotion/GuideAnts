using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.IO.Compression;
using System.Text.Json;

namespace GuideAntsApi.Tests.Services.SystemGuide;

[TestClass]
public sealed class GuideAntsSystemSettingsStoreTests
{
    [TestMethod]
    public async Task SaveAsync_round_trips_all_ids()
    {
        await using var db = CreateDbContext();
        await SeedGuideAntsSystemSectionAsync(db);

        var store = CreateStore(db);
        var expected = new GuideAntsSystemSettings
        {
            ProjectId = Guid.NewGuid(),
            UserGuideId = Guid.NewGuid(),
            AdminGuideId = Guid.NewGuid(),
            UserNotebookId = Guid.NewGuid(),
            AdminNotebookId = Guid.NewGuid(),
            UserPublishedGuideId = Guid.NewGuid(),
            AdminPublishedGuideId = Guid.NewGuid(),
            ClientBridgeId = GuideAntsSystemSettings.DefaultClientBridgeId
        };

        await store.SaveAsync(expected);

        var loaded = await store.GetAsync();
        loaded.Should().NotBeNull();
        loaded!.ProjectId.Should().Be(expected.ProjectId);
        loaded.UserGuideId.Should().Be(expected.UserGuideId);
        loaded.AdminGuideId.Should().Be(expected.AdminGuideId);
        loaded.UserNotebookId.Should().Be(expected.UserNotebookId);
        loaded.AdminNotebookId.Should().Be(expected.AdminNotebookId);
        loaded.UserPublishedGuideId.Should().Be(expected.UserPublishedGuideId);
        loaded.AdminPublishedGuideId.Should().Be(expected.AdminPublishedGuideId);
        loaded.ClientBridgeId.Should().Be(GuideAntsSystemSettings.DefaultClientBridgeId);
        loaded.IsFullySeeded.Should().BeTrue();
    }

    private static GuideAntsSystemSettingsStore CreateStore(ApplicationDbContext db)
    {
        var settingsService = new Mock<IApplicationSettingsService>();
        settingsService.Setup(x => x.ReloadConfiguration());
        return new GuideAntsSystemSettingsStore(
            db,
            new SettingsSectionRegistry(),
            settingsService.Object);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"gg-settings-{Guid.NewGuid():N}");
        return new ApplicationDbContext(options);
    }

    private static async Task SeedGuideAntsSystemSectionAsync(ApplicationDbContext db)
    {
        db.ApplicationSettings.Add(new ApplicationSetting
        {
            SectionName = GuideAntsSystemSettings.SectionName,
            SchemaVersion = 1,
            JsonValue = "{\"clientBridgeId\":\"guideants-app\"}",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}

[TestClass]
public sealed class GuideAntsSystemSeederTests
{
    [TestMethod]
    public async Task SeedAsync_first_run_creates_system_entities_with_app_identity_published_rows()
    {
        await using var db = CreateDbContext();
        await SeedGuideAntsSystemSectionAsync(db);
        await SeedGuidesAsync(db);

        var seeder = CreateSeeder(db);
        await seeder.SeedAsync();

        var settings = await CreateStore(db).GetAsync();
        settings.Should().NotBeNull();
        settings!.IsFullySeeded.Should().BeTrue();

        var project = await db.Projects.SingleAsync(p => p.IsSystemProject);
        project.Slug.Should().Be(GuideAntsSystemSeeder.SystemProjectSlug);
        project.Title.Should().Be(GuideAntsSystemSeeder.SystemProjectTitle);

        var guides = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide
                        && (a.Name == GuideAntsSystemSeeder.UserGuideName
                            || a.Name == GuideAntsSystemSeeder.AdminGuideName))
            .ToListAsync();
        guides.Should().HaveCount(2);

        var notebooks = await db.Notebooks.Where(n => n.ProjectId == project.Id).ToListAsync();
        notebooks.Should().HaveCount(2);

        var publishedGuides = await db.PublishedGuides
            .Include(pg => pg.Notebook)
            .Where(pg => pg.Notebook.ProjectId == project.Id)
            .ToListAsync();
        publishedGuides.Should().HaveCount(2);
        publishedGuides.Should().OnlyContain(pg =>
            pg.Active
            && pg.CommandMode
            && pg.DisplayMode == "full"
            && pg.ShowTurnNavigation
            && !pg.Collapsible
            && pg.AuthMode == PublishedGuideAuthMode.AppIdentity
            && pg.FriendlyName == null
            && pg.MaxTurns == 50
            && pg.DailyChargeLimitUsd == null
            && pg.BillingPeriodChargeLimitUsd == null);
    }

    [TestMethod]
    public async Task SeedAsync_second_run_creates_no_duplicates()
    {
        await using var db = CreateDbContext();
        await SeedGuideAntsSystemSectionAsync(db);
        await SeedGuidesAsync(db);

        var seeder = CreateSeeder(db);
        await seeder.SeedAsync();
        await seeder.SeedAsync();

        var projectCount = await db.Projects.CountAsync(p => p.IsSystemProject && !p.Deleted);
        projectCount.Should().Be(1);

        var project = await db.Projects.SingleAsync(p => p.IsSystemProject);

        var publishedCount = await db.PublishedGuides
            .Include(pg => pg.Notebook)
            .CountAsync(pg => pg.Notebook.ProjectId == project.Id);
        publishedCount.Should().Be(2);

        var notebookCount = await db.Notebooks.CountAsync(n => n.ProjectId == project.Id);
        notebookCount.Should().Be(2);
    }

    [TestMethod]
    public async Task SeedAsync_repairs_missing_published_row()
    {
        await using var db = CreateDbContext();
        await SeedGuideAntsSystemSectionAsync(db);
        await SeedGuidesAsync(db);

        var seeder = CreateSeeder(db);
        await seeder.SeedAsync();

        var settings = await CreateStore(db).GetAsync();
        settings.Should().NotBeNull();

        var adminPublishedId = settings!.AdminPublishedGuideId!.Value;
        db.PublishedGuides.Remove(await db.PublishedGuides.SingleAsync(pg => pg.Id == adminPublishedId));
        await db.SaveChangesAsync();

        await seeder.SeedAsync();

        var projectId = settings.ProjectId!.Value;
        var publishedGuides = await db.PublishedGuides
            .Include(pg => pg.Notebook)
            .Where(pg => pg.Notebook.ProjectId == projectId)
            .ToListAsync();
        publishedGuides.Should().HaveCount(2);
        publishedGuides.Should().OnlyContain(pg => pg.AuthMode == PublishedGuideAuthMode.AppIdentity);

        var refreshedSettings = await CreateStore(db).GetAsync();
        refreshedSettings!.AdminPublishedGuideId.Should().NotBe(adminPublishedId);
    }

    private static GuideAntsSystemSeeder CreateSeeder(ApplicationDbContext db)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.Setup(x => x.ContentRootPath).Returns(ResolveGuideAntsApiContentRoot());

        var importService = new Mock<IGuideExportImportService>();
        importService
            .Setup(x => x.ImportGuideAsync(It.IsAny<Stream>()))
            .Returns((Stream stream) => ResolveImportResultAsync(db, stream));

        return new GuideAntsSystemSeeder(
            environment.Object,
            db,
            importService.Object,
            CreateStore(db),
            NullLogger<GuideAntsSystemSeeder>.Instance);
    }

    private static async Task<ImportGuideResultDto> ResolveImportResultAsync(ApplicationDbContext db, Stream stream)
    {
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("manifest.json not found in bootstrap stream.");

        await using var manifestStream = manifestEntry.Open();
        using var reader = new StreamReader(manifestStream);
        using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        var guideName = doc.RootElement.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Bootstrap guide manifest is missing name.");

        var existingGuide = await db.Assistants
            .FirstOrDefaultAsync(a => a.Name == guideName && a.Kind == AssistantKind.Guide);

        var guideId = existingGuide?.Id ?? Guid.NewGuid();
        if (existingGuide == null)
        {
            db.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = guideName,
                Kind = AssistantKind.Guide,
                IsGlobal = true,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        return new ImportGuideResultDto(true, guideId, 0, 0, 0, 0, []);
    }

    private static GuideAntsSystemSettingsStore CreateStore(ApplicationDbContext db)
    {
        var settingsService = new Mock<IApplicationSettingsService>();
        settingsService.Setup(x => x.ReloadConfiguration());
        return new GuideAntsSystemSettingsStore(
            db,
            new SettingsSectionRegistry(),
            settingsService.Object);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"gg-seeder-{Guid.NewGuid():N}");
        return new ApplicationDbContext(options);
    }

    private static async Task SeedGuideAntsSystemSectionAsync(ApplicationDbContext db)
    {
        db.ApplicationSettings.Add(new ApplicationSetting
        {
            SectionName = GuideAntsSystemSettings.SectionName,
            SchemaVersion = 1,
            JsonValue = "{\"clientBridgeId\":\"guideants-app\"}",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedGuidesAsync(ApplicationDbContext db)
    {
        db.Assistants.AddRange(
            new Assistant
            {
                Name = GuideAntsSystemSeeder.UserGuideName,
                Kind = AssistantKind.Guide,
                IsGlobal = true,
                IsActive = true
            },
            new Assistant
            {
                Name = GuideAntsSystemSeeder.AdminGuideName,
                Kind = AssistantKind.Guide,
                IsGlobal = true,
                IsActive = true
            });
        await db.SaveChangesAsync();
    }

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
}
