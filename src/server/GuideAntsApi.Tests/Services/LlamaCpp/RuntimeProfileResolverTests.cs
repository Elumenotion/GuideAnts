using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public class RuntimeProfileResolverTests
{
    private ApplicationDbContext _context = null!;
    private RuntimeProfileResolver _resolver = null!;

    private const string SamplingParametersJson = """
        {
          "temperature": { "key": "temperature", "label": "Temperature", "description": "Controls randomness", "min": 0.0, "max": 2.0, "step": 0.1, "default": 0.7, "displayOrder": 0, "exposedInGuideBuilder": true },
          "top_p": { "key": "top_p", "label": "Top P", "description": "Controls diversity", "min": 0.0, "max": 1.0, "step": 0.05, "default": 0.8, "displayOrder": 1, "exposedInGuideBuilder": true }
        }
        """;

    private const string ThinkingControlJson = """
        {
          "defaultChoice": "enabled",
          "choiceActions": {
            "none": [
              { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": false },
              { "target": "RequestField", "key": "reasoning_format", "value": "none" }
            ],
            "enabled": [
              { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": true }
            ]
          }
        }
        """;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new ApplicationDbContext(options);

        var scopeFactory = CreateScopeFactory(_context);
        _resolver = new RuntimeProfileResolver(scopeFactory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [TestMethod]
    public async Task ResolveAsync_ReturnsProfileData_ForExistingProfile()
    {
        _context.RuntimeProfiles.Add(new RuntimeProfile
        {
            ProfileId = "qwen3_5",
            DisplayName = "Qwen 3.5",
            Description = "Qwen 3.5 runtime profile",
            CombineSystemAndDeveloperMessages = true,
            ThoughtBlockPattern = @"<think>[\s\S]*?</think>",
            SamplingParametersJson = SamplingParametersJson,
            ThinkingControlJson = ThinkingControlJson
        });
        await _context.SaveChangesAsync();

        var result = await _resolver.ResolveAsync("qwen3_5");

        result.ProfileId.Should().Be("qwen3_5");
        result.CombineSystemAndDeveloperMessages.Should().BeTrue();
        result.ThoughtBlockPattern.Should().Be(@"<think>[\s\S]*?</think>");
        result.SamplingParameters.Should().HaveCount(2);
        result.ThinkingControl.DefaultChoice.Should().Be("enabled");
        result.ThinkingControl.ChoiceActions.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ResolveAsync_ThrowsInvalidOperationException_ForUnknownProfile()
    {
        var act = () => _resolver.ResolveAsync("nonexistent_profile");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*nonexistent_profile*not found*");
    }

    [TestMethod]
    public async Task ResolveAsync_DeserializesSamplingParameters_FromJson()
    {
        _context.RuntimeProfiles.Add(new RuntimeProfile
        {
            ProfileId = "test_sampling",
            DisplayName = "Test Sampling",
            CombineSystemAndDeveloperMessages = false,
            SamplingParametersJson = SamplingParametersJson,
            ThinkingControlJson = """{"defaultChoice":"enabled","choiceActions":{}}"""
        });
        await _context.SaveChangesAsync();

        var result = await _resolver.ResolveAsync("test_sampling");

        result.SamplingParameters.Should().ContainKeys("temperature", "top_p");

        var temp = result.SamplingParameters["temperature"];
        temp.Key.Should().Be("temperature");
        temp.Label.Should().Be("Temperature");
        temp.Description.Should().Be("Controls randomness");
        temp.Min.Should().Be(0.0);
        temp.Max.Should().Be(2.0);
        temp.Step.Should().Be(0.1);
        temp.Default.Should().Be(0.7);
        temp.DisplayOrder.Should().Be(0);
        temp.ExposedInGuideBuilder.Should().BeTrue();

        var topP = result.SamplingParameters["top_p"];
        topP.Key.Should().Be("top_p");
        topP.Label.Should().Be("Top P");
        topP.Description.Should().Be("Controls diversity");
        topP.Min.Should().Be(0.0);
        topP.Max.Should().Be(1.0);
        topP.Step.Should().Be(0.05);
        topP.Default.Should().Be(0.8);
        topP.DisplayOrder.Should().Be(1);
        topP.ExposedInGuideBuilder.Should().BeTrue();
    }

    [TestMethod]
    public async Task ResolveAsync_DeserializesThinkingControl_FromJson()
    {
        _context.RuntimeProfiles.Add(new RuntimeProfile
        {
            ProfileId = "test_thinking",
            DisplayName = "Test Thinking",
            CombineSystemAndDeveloperMessages = true,
            SamplingParametersJson = "{}",
            ThinkingControlJson = ThinkingControlJson
        });
        await _context.SaveChangesAsync();

        var result = await _resolver.ResolveAsync("test_thinking");

        result.ThinkingControl.DefaultChoice.Should().Be("enabled");
        result.ThinkingControl.ChoiceActions.Should().ContainKeys("none", "enabled");

        var noneActions = result.ThinkingControl.ChoiceActions["none"];
        noneActions.Should().HaveCount(2);
        noneActions[0].Target.Should().Be(ThinkingActionTarget.NestedRequestField);
        noneActions[0].Key.Should().Be("chat_template_kwargs.enable_thinking");
        noneActions[1].Target.Should().Be(ThinkingActionTarget.RequestField);
        noneActions[1].Key.Should().Be("reasoning_format");

        var enabledActions = result.ThinkingControl.ChoiceActions["enabled"];
        enabledActions.Should().HaveCount(1);
        enabledActions[0].Target.Should().Be(ThinkingActionTarget.NestedRequestField);
        enabledActions[0].Key.Should().Be("chat_template_kwargs.enable_thinking");
    }

    [TestMethod]
    public async Task InvalidateCache_CausesNextResolveToReloadFromDb()
    {
        _context.RuntimeProfiles.Add(new RuntimeProfile
        {
            ProfileId = "cached_profile",
            DisplayName = "Original Name",
            CombineSystemAndDeveloperMessages = false,
            ThoughtBlockPattern = null,
            SamplingParametersJson = "{}",
            ThinkingControlJson = """{"defaultChoice":"none","choiceActions":{}}"""
        });
        await _context.SaveChangesAsync();

        var first = await _resolver.ResolveAsync("cached_profile");
        first.CombineSystemAndDeveloperMessages.Should().BeFalse();
        first.ThinkingControl.DefaultChoice.Should().Be("none");

        var entity = await _context.RuntimeProfiles.SingleAsync(p => p.ProfileId == "cached_profile");
        entity.CombineSystemAndDeveloperMessages = true;
        entity.ThinkingControlJson = """{"defaultChoice":"enabled","choiceActions":{}}""";
        await _context.SaveChangesAsync();

        _resolver.InvalidateCache();
        var second = await _resolver.ResolveAsync("cached_profile");

        second.CombineSystemAndDeveloperMessages.Should().BeTrue();
        second.ThinkingControl.DefaultChoice.Should().Be("enabled");
    }

    private static IServiceScopeFactory CreateScopeFactory(ApplicationDbContext context)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(ApplicationDbContext)))
            .Returns(context);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return factory.Object;
    }
}
