using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.DataModel;

[TestClass]
public sealed class ModelContextWindowTests
{
    [TestMethod]
    public async Task ContextWindowColumns_RoundTrip()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"ctxwin-{Guid.NewGuid():N}");

        await using (var db = new ApplicationDbContext(options))
        {
            db.Models.Add(new Model
            {
                ModelId = "test-model",
                DisplayName = "Test Model",
                Provider = "openai-chat",
                ContextWindowTokens = 200_000,
                MaxOutputTokens = 64_000
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ApplicationDbContext(options))
        {
            var saved = await db.Models.SingleAsync(m => m.ModelId == "test-model");
            saved.ContextWindowTokens.Should().Be(200_000);
            saved.MaxOutputTokens.Should().Be(64_000);
        }
    }

    [TestMethod]
    public async Task ContextWindowColumns_DefaultToNull()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"ctxwin-null-{Guid.NewGuid():N}");

        await using (var db = new ApplicationDbContext(options))
        {
            db.Models.Add(new Model
            {
                ModelId = "unknown-model",
                DisplayName = "Unknown Model",
                Provider = "openrouter-chat"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ApplicationDbContext(options))
        {
            var saved = await db.Models.SingleAsync(m => m.ModelId == "unknown-model");
            saved.ContextWindowTokens.Should().BeNull();
            saved.MaxOutputTokens.Should().BeNull();
        }
    }
}
