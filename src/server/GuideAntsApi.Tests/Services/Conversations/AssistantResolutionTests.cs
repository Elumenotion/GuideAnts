using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class AssistantResolutionTests
{
    private ApplicationDbContext _dbContext = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);
    }

    [TestCleanup]
    public void TestCleanup() => _dbContext.Dispose();

    private Guid SeedAssistant(string name, bool isActive, bool isGlobal)
    {
        var assistant = new Assistant { Id = Guid.NewGuid(), Name = name, IsActive = isActive, IsGlobal = isGlobal };
        _dbContext.Assistants.Add(assistant);
        _dbContext.SaveChanges();
        return assistant.Id;
    }

    [TestMethod]
    public async Task Resolve_PrefersActiveNonGlobalOverGlobal_AndIgnoresInactive()
    {
        SeedAssistant("Claude", isActive: false, isGlobal: false);
        var globalId = SeedAssistant("Claude", isActive: true, isGlobal: true);
        var localId = SeedAssistant("Claude", isActive: true, isGlobal: false);

        var resolved = await AssistantResolution.ResolveActiveAssistantIdAsync(_dbContext, "Claude", CancellationToken.None);

        resolved.Should().Be(localId);
        resolved.Should().NotBe(globalId);
    }

    [TestMethod]
    public async Task Resolve_UnknownName_ReturnsNull()
    {
        var resolved = await AssistantResolution.ResolveActiveAssistantIdAsync(_dbContext, "Nope", CancellationToken.None);
        resolved.Should().BeNull();
    }
}
