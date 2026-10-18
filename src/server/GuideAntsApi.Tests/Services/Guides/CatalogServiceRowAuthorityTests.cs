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

    [TestMethod]
    public async Task GetModelsAsync_CarriesContextWindowAndMaxOutputTokensFromRow()
    {
        await using var context = CreateContext();
        context.Models.Add(new Model
        {
            ModelId = "ctx-model",
            DisplayName = "Ctx",
            Provider = "openai-chat",
            SamplingParametersJson = "{}",
            ContextWindowTokens = 262_147,
            MaxOutputTokens = 16_381,
            IsActive = true,
        });
        context.Models.Add(new Model
        {
            ModelId = "no-ctx-model",
            DisplayName = "NoCtx",
            Provider = "openai-chat",
            SamplingParametersJson = "{}",
            IsActive = true,
        });
        await context.SaveChangesAsync();

        var models = (await new CatalogService(context).GetModelsAsync()).ToList();

        var withValues = models.Single(m => m.ModelId == "ctx-model");
        withValues.ContextWindowTokens.Should().Be(262_147);
        withValues.MaxOutputTokens.Should().Be(16_381);
        var withoutValues = models.Single(m => m.ModelId == "no-ctx-model");
        withoutValues.ContextWindowTokens.Should().BeNull();
        withoutValues.MaxOutputTokens.Should().BeNull();
    }

    [TestMethod]
    public async Task GetModelsAsync_NonLocalModel_HonorsThinkingControlDefaultChoice()
    {
        await using var context = CreateContext();
        context.Models.Add(new Model
        {
            ModelId = "openai-compatible-qwen",
            DisplayName = "Qwen (OpenAI-compatible)",
            Provider = "openai-compatible",
            SamplingParametersJson = "{}",
            ReasoningChoicesJson = "[\"none\",\"low\",\"medium\",\"high\"]",
            ThinkingControlJson = "{\"defaultChoice\":\"high\"}",
            IsActive = true,
        });
        context.Models.Add(new Model
        {
            ModelId = "openrouter-qwen",
            DisplayName = "Qwen (OpenRouter)",
            Provider = "openrouter-chat",
            SamplingParametersJson = "{}",
            ReasoningChoicesJson = "[\"none\",\"low\",\"medium\",\"high\"]",
            ThinkingControlJson = "{\"defaultChoice\":\"high\",\"choiceActions\":{}}",
            IsActive = true,
        });
        await context.SaveChangesAsync();

        var models = (await new CatalogService(context).GetModelsAsync()).ToList();

        // A declared defaultChoice that is one of the row's choices wins over the first choice,
        // for the same providers whose clients already honor it at inference.
        models.Single(m => m.ModelId == "openai-compatible-qwen").DefaultReasoningChoice.Should().Be("high");
        models.Single(m => m.ModelId == "openrouter-qwen").DefaultReasoningChoice.Should().Be("high");
    }

    [TestMethod]
    public async Task GetModelsAsync_NonLocalModel_DefaultChoiceNotInChoices_FallsBackToFirstChoice()
    {
        await using var context = CreateContext();
        context.Models.Add(new Model
        {
            ModelId = "openai-compatible-bad-default",
            DisplayName = "Bad Default",
            Provider = "openai-compatible",
            SamplingParametersJson = "{}",
            ReasoningChoicesJson = "[\"none\",\"low\"]",
            ThinkingControlJson = "{\"defaultChoice\":\"extreme\"}",
            IsActive = true,
        });
        await context.SaveChangesAsync();

        var models = (await new CatalogService(context).GetModelsAsync()).ToList();
        models.Single(m => m.ModelId == "openai-compatible-bad-default")
            .DefaultReasoningChoice.Should().Be("none");
    }

    [TestMethod]
    public async Task GetModelsAsync_NonLocalModel_MalformedThinkingControl_FallsBackToFirstChoice()
    {
        await using var context = CreateContext();
        context.Models.Add(new Model
        {
            ModelId = "openai-compatible-malformed",
            DisplayName = "Malformed",
            Provider = "openai-compatible",
            SamplingParametersJson = "{}",
            ReasoningChoicesJson = "[\"none\",\"low\"]",
            ThinkingControlJson = "{not json",
            IsActive = true,
        });
        await context.SaveChangesAsync();

        var models = (await new CatalogService(context).GetModelsAsync()).ToList();
        models.Single(m => m.ModelId == "openai-compatible-malformed")
            .DefaultReasoningChoice.Should().Be("none");
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
