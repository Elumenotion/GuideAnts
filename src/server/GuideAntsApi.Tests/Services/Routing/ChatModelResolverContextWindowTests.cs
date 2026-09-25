using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Settings;
using Moq;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class ChatModelResolverContextWindowTests
{
    [TestMethod]
    public void ResolvedExecutionPolicy_CarriesContextWindow()
    {
        var policy = new ResolvedExecutionPolicy(
            "test-model",
            "openai-chat",
            ParameterAuthority.AssistantDefinition,
            new Dictionary<string, System.Text.Json.JsonElement>(),
            ContextWindowTokens: 200_000,
            MaxOutputTokens: 64_000);

        policy.ContextWindowTokens.Should().Be(200_000);
        policy.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public void ResolvedExecutionPolicy_DefaultsToNull_WhenNotSupplied()
    {
        var policy = new ResolvedExecutionPolicy(
            "test-model",
            "openai-chat",
            ParameterAuthority.AssistantDefinition,
            new Dictionary<string, System.Text.Json.JsonElement>());

        policy.ContextWindowTokens.Should().BeNull();
        policy.MaxOutputTokens.Should().BeNull();
    }

    private static ChatModelResolver BuildResolver(ChatTarget target)
    {
        var store = new Mock<IChatDefaultsStore>();
        store.SetupGet(s => s.Current).Returns(ChatDefaultsSnapshot.Empty);
        var targets = new Mock<IChatTargetResolver>();
        targets.Setup(t => t.Resolve(It.IsAny<string?>())).Returns(target);
        return new ChatModelResolver(store.Object, targets.Object);
    }

    [TestMethod]
    public void Resolve_CopiesContextWindowValuesFromChatTargetIntoPolicy()
    {
        var resolver = BuildResolver(new ChatTarget("m", "openai-responses", null, null, 200_000, 64_000));

        var result = resolver.Resolve("m");

        result.ExecutionPolicy.ContextWindowTokens.Should().Be(200_000);
        result.ExecutionPolicy.MaxOutputTokens.Should().Be(64_000);
    }

    [TestMethod]
    public void Resolve_LeavesPolicyValuesNull_WhenChatTargetHasNone()
    {
        var resolver = BuildResolver(new ChatTarget("m", "openai-responses", null));

        var result = resolver.Resolve("m");

        result.ExecutionPolicy.ContextWindowTokens.Should().BeNull();
        result.ExecutionPolicy.MaxOutputTokens.Should().BeNull();
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static Model CatalogRow(int? contextWindow, int? maxOutput) => new()
    {
        ModelId = "windowed-model",
        DisplayName = "Windowed Model",
        Provider = "openai-responses",
        IsActive = true,
        Created = DateTime.UtcNow,
        ContextWindowTokens = contextWindow,
        MaxOutputTokens = maxOutput,
    };

    [TestMethod]
    public void ChatTargetResolver_CarriesContextWindowValuesFromCatalogRow()
    {
        using var db = CreateDb();
        db.Models.Add(CatalogRow(272_144, 40_960));
        db.SaveChanges();

        var target = new ChatTargetResolver(new TestServiceScopeFactory(db)).Resolve("windowed-model");

        target.ContextWindowTokens.Should().Be(272_144);
        target.MaxOutputTokens.Should().Be(40_960);
    }

    [TestMethod]
    public void ChatTargetResolver_LeavesContextWindowValuesNull_WhenCatalogRowHasNone()
    {
        using var db = CreateDb();
        db.Models.Add(CatalogRow(null, null));
        db.SaveChanges();

        var target = new ChatTargetResolver(new TestServiceScopeFactory(db)).Resolve("windowed-model");

        target.ContextWindowTokens.Should().BeNull();
        target.MaxOutputTokens.Should().BeNull();
    }
}
