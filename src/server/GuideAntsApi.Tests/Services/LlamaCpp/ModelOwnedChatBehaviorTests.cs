using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.Services.Guides;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class ModelOwnedChatBehaviorTests
{
    private const string ThinkingControlJson = """
        {"defaultChoice":"none","choiceActions":{"none":[],"medium":[]}}
        """;

    [TestMethod]
    public void ChatTargetResolver_LoadsBehaviorFromModelColumns()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"qwen-local"}""",
            CombineSystemAndDeveloperMessages = true,
            ThoughtBlockPattern = "<think>",
            SamplingParametersJson = "{}",
            ThinkingControlJson = ThinkingControlJson,
            RequestFieldsWhenToolsPresentJson = """{"parallel_tool_calls":false}""",
            IsActive = true,
            Created = DateTime.UtcNow,
        });
        db.SaveChanges();

        var resolver = new ChatTargetResolver(new TestServiceScopeFactory(db));
        var target = resolver.Resolve("qwen-local");

        target.LlamaChatBehavior.Should().NotBeNull();
        target.LlamaChatBehavior!.ThinkingControl.ChoiceActions.Should().ContainKeys("none", "medium");
        target.LlamaChatBehavior.ThoughtBlockPattern.Should().Be("<think>");
    }

    [TestMethod]
    public void RoutingChatCompletionClientFactory_DoesNotDependOnRuntimeProfileResolver()
    {
        var constructor = typeof(RoutingChatCompletionClientFactory).GetConstructors().Single();
        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        parameterTypes.Should().NotContain(typeof(IRuntimeProfileResolver));
    }

    [TestMethod]
    public async Task CreateGuideAsync_LlamaModel_UsesModelOwnedBehavior_NotRuntimeProfileResolver()
    {
        await using var context = CreateDb();
        context.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"qwen-local"}""",
            ReasoningChoicesJson = """["none","medium"]""",
            CombineSystemAndDeveloperMessages = true,
            SamplingParametersJson = "{}",
            ThinkingControlJson = ThinkingControlJson,
            RequestFieldsWhenToolsPresentJson = "{}",
            IsActive = true,
            Created = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>(MockBehavior.Strict);
        var service = GuidesServiceTestHelper.CreateGuidesService(context, runtimeProfileResolver.Object);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "qwen-local", ReasoningEffort = "medium" };

        var created = await service.CreateGuideAsync(dto);
        var details = await service.GetGuideAsync(created.Id);
        details!.ReasoningEffort.Should().Be("medium");
        runtimeProfileResolver.Verify(
            r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task CreateGuideAsync_LlamaModel_RejectsReasoningEffortMissingFromModelChoices()
    {
        await using var context = CreateDb();
        context.Models.Add(new Model
        {
            ModelId = "qwen-local",
            DisplayName = "Qwen Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"qwen-local"}""",
            ReasoningChoicesJson = """["none"]""",
            CombineSystemAndDeveloperMessages = true,
            SamplingParametersJson = "{}",
            ThinkingControlJson = ThinkingControlJson,
            RequestFieldsWhenToolsPresentJson = "{}",
            IsActive = true,
            Created = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "qwen-local", ReasoningEffort = "medium" };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Reasoning effort 'medium' is invalid*");
    }

    private static CreateGuideDto MinimalCreateGuideDto(string name) =>
        new(
            Name: name,
            Description: "desc",
            Instructions: "helpful",
            HomePageMarkdown: "# Home",
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            AuthProviders: null,
            Files: null,
            ConversationStarters: null,
            CrewMemberIds: null);

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
