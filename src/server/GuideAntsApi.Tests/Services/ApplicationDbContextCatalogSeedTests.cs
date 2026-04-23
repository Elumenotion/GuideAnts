using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class ApplicationDbContextCatalogSeedTests
{
    [TestMethod]
    public void ExcludedHost_ModelConfig_HasExpectedIndexes()
    {
        using var context = CreateContext();

        var entity = context.Model.FindEntityType(typeof(ExcludedHost));
        entity.Should().NotBeNull();

        var hostIndex = entity!.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(["Host"]));
        hostIndex.IsUnique.Should().BeTrue();

        var createdIndex = entity.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(["Created"]));
        createdIndex.IsUnique.Should().BeFalse();
    }

    [TestMethod]
    public async Task ToolSeed_IncludesWebSearch_FirstInSearchCategory()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        var searchTools = await context.Tools
            .Where(t => t.Category == "Search")
            .OrderBy(t => t.DisplayOrder)
            .ThenBy(t => t.DisplayName)
            .ToListAsync();

        searchTools.Should().NotBeEmpty();
        searchTools[0].ToolType.Should().Be("WebSearch");
        searchTools[0].DisplayName.Should().Be("Web Search");

        searchTools.Select(t => t.ToolType).Should().ContainInOrder("WebSearch", "ReadWeb", "search_notebook", "search_project");
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }
}
