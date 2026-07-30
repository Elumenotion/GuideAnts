using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class CatalogServiceRowAuthorityTests
{
    private const string OpenAiChatSampling = """
        {
          "temperature": { "key": "temperature", "label": "Temperature", "description": "", "min": 0, "max": 2, "step": 0.1, "default": 1, "displayOrder": 0, "exposedInGuideBuilder": true },
          "top_p": { "key": "top_p", "label": "Top P", "description": "", "min": 0, "max": 1, "step": 0.05, "default": 1, "displayOrder": 1, "exposedInGuideBuilder": true }
        }
        """;

    [TestMethod]
    public async Task GetModelsAsync_NonLocalModel_UsesRowOwnedSamplingAndReasoningChoices()
    {
        await using var context = CreateContext();
        context.Models.Add(new Model
        {
            ModelId = "gpt-x",
            DisplayName = "GPT X",
            Provider = "openai-chat",
            SamplingParametersJson = OpenAiChatSampling,
            ReasoningChoicesJson = """["low","medium","high"]""",
            RuntimeConfigJson = """{"runtimeProfileId":"openai_chat_standard"}""",
            IsActive = true,
        });
        await context.SaveChangesAsync();

        var service = new CatalogService(context);
        var models = (await service.GetModelsAsync()).ToList();

        var model = models.Single(m => m.ModelId == "gpt-x");
        model.SamplingParameterPolicy.Should().NotBeNull();
        model.SamplingParameterPolicy!.Select(p => p.Key).Should().Contain(["temperature", "top_p"]);
        model.ReasoningChoices.Should().Equal("low", "medium", "high");
        model.DefaultReasoningChoice.Should().Be("low");
        model.RuntimeConfig.Should().BeNull();
    }

    [TestMethod]
    public async Task GetModelsAsync_NonLocalModel_DoesNotRequireRuntimeProfileResolution()
    {
        await using var context = CreateContext();
        context.Models.Add(new Model
        {
            ModelId = "o3",
            DisplayName = "o3",
            Provider = "openai-responses",
            SamplingParametersJson = "{}",
            ReasoningChoicesJson = """["none","low","medium","high"]""",
            IsActive = true,
        });
        await context.SaveChangesAsync();

        var service = new CatalogService(context);
        var models = (await service.GetModelsAsync()).ToList();

        var model = models.Single(m => m.ModelId == "o3");
        model.SamplingParameterPolicy.Should().BeNull();
        model.ReasoningChoices.Should().Equal("none", "low", "medium", "high");
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
