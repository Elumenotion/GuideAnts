using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class LlmProviderResolverTests
{
    [TestMethod]
    public void ResolveUsageServiceName_ResolvesEveryKnownChatProvider()
    {
        var expectedByProvider = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai-chat"] = "OpenAI",
            ["openai-responses"] = "OpenAI",
            ["azure-openai-chat"] = "AzureOpenAI",
            ["azure-openai-responses"] = "AzureOpenAI",
            ["anthropic"] = "Anthropic",
            ["llama-cpp"] = "LocalLlamaCpp",
            ["google-gemini-chat"] = "GoogleGemini",
            ["hf-inference-chat"] = "HuggingFace",
            ["openrouter-chat"] = "OpenRouter"
        };

        expectedByProvider.Keys
            .Should()
            .BeEquivalentTo(ChatTargetValidator.KnownProviders);

        using var db = CreateDb();
        foreach (var provider in expectedByProvider.Keys)
        {
            db.Models.Add(new Model
            {
                ModelId = BuildModelId(provider),
                DisplayName = $"Model for {provider}",
                Provider = provider,
                IsActive = true
            });
        }
        db.SaveChanges();

        var scopeFactory = new TestServiceScopeFactory(db);

        foreach (var (provider, expectedServiceName) in expectedByProvider)
        {
            var modelId = BuildModelId(provider);

            var actualServiceName = LlmProviderResolver.ResolveUsageServiceName(modelId, scopeFactory);

            actualServiceName.Should().Be(expectedServiceName, $"provider '{provider}' should map to usage service '{expectedServiceName}'");
        }
    }

    [TestMethod]
    public void ResolveUsageServiceName_ThrowsForUnsupportedProvider()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "unsupported-provider-model",
            DisplayName = "Unsupported",
            Provider = "not-a-real-provider",
            IsActive = true
        });
        db.SaveChanges();

        var scopeFactory = new TestServiceScopeFactory(db);

        Action act = () => LlmProviderResolver.ResolveUsageServiceName("unsupported-provider-model", scopeFactory);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Unsupported model provider 'not-a-real-provider'*");
    }

    [TestMethod]
    public void ResolveUsageServiceName_ThrowsWhenModelMissing()
    {
        using var db = CreateDb();
        var scopeFactory = new TestServiceScopeFactory(db);

        Action act = () => LlmProviderResolver.ResolveUsageServiceName("missing-model", scopeFactory);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Model 'missing-model' not found in model catalog.");
    }

    private static string BuildModelId(string provider) => $"usage-test-{provider}";

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
