using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Bootstrap;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class RuntimeProfileSeederPhase1BTests
{
    [TestMethod]
    public async Task SeedAsync_InsertsNewProfiles_WithRequestFieldsWhenToolsPresent()
    {
        await using var db = CreateDbContext();
        var seeder = CreateSeeder(db);

        await seeder.SeedAsync();

        var deepseek = await db.RuntimeProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.ProfileId == "deepseek_r1");
        deepseek.Should().NotBeNull();
        deepseek!.RequestFieldsWhenToolsPresentJson.Should().Contain("parallel_tool_calls");

        var coder = await db.RuntimeProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.ProfileId == "qwen3_coder");
        coder.Should().NotBeNull();
        coder!.RequestFieldsWhenToolsPresentJson.Should().Contain("true");

        var qwen35 = await db.RuntimeProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.ProfileId == "qwen3_5");
        qwen35.Should().NotBeNull();
        qwen35!.RequestFieldsWhenToolsPresentJson.Should().Contain("parallel_tool_calls");
    }

    [TestMethod]
    public async Task SeedAsync_SkipsExistingProfiles_DoesNotOverwriteRequestFields()
    {
        await using var db = CreateDbContext();
        db.RuntimeProfiles.Add(new RuntimeProfile
        {
            ProfileId = "qwen3_5",
            DisplayName = "Operator Qwen 3.5",
            CombineSystemAndDeveloperMessages = true,
            SamplingParametersJson = "{}",
            ThinkingControlJson = "{}",
            RequestFieldsWhenToolsPresentJson = """{"parallel_tool_calls":false}""",
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var seeder = CreateSeeder(db);
        await seeder.SeedAsync();

        var profile = await db.RuntimeProfiles.AsNoTracking().SingleAsync(p => p.ProfileId == "qwen3_5");
        profile.DisplayName.Should().Be("Operator Qwen 3.5");
        profile.RequestFieldsWhenToolsPresentJson.Should().Be("""{"parallel_tool_calls":false}""");
    }

    [TestMethod]
    public async Task SeedAsync_IsIdempotent_OnSecondRun()
    {
        await using var db = CreateDbContext();
        var seeder = CreateSeeder(db);

        await seeder.SeedAsync();
        var countAfterFirst = await db.RuntimeProfiles.CountAsync();

        await seeder.SeedAsync();
        var countAfterSecond = await db.RuntimeProfiles.CountAsync();

        countAfterSecond.Should().Be(countAfterFirst);
    }

    private static RuntimeProfileSeeder CreateSeeder(ApplicationDbContext db)
    {
        var environment = new Mock<IWebHostEnvironment>();
        var contentRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "GuideAntsApi"));
        environment.SetupGet(e => e.ContentRootPath).Returns(contentRoot);

        return new RuntimeProfileSeeder(environment.Object, db, NullLogger<RuntimeProfileSeeder>.Instance);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"phase1b-seeder-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }
}
