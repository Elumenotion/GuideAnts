using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Anthropic;
using AntRunner.Chat.LlamaCpp;
using AntRunner.Chat.OpenAI;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using ThinkingAction = GuideAntsApi.Services.LlamaCpp.ThinkingAction;
using ThinkingActionTarget = GuideAntsApi.Services.LlamaCpp.ThinkingActionTarget;
using ThinkingControl = GuideAntsApi.Services.LlamaCpp.ThinkingControl;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class RoutingChatCompletionClientFactoryTests
{
    [TestMethod]
    public void CreateClient_UsesAnthropic_ForAnthropicProvider()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet 4.5",
            Provider = "anthropic",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);
        var client = factory.CreateClient("claude-sonnet-4-5");

        client.Should().BeOfType<AnthropicChatClient>();
    }

    [TestMethod]
    public void CreateClient_Throws_WhenDeploymentIdMissing()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "gpt-4.1",
            DisplayName = "GPT 4.1",
            Provider = "openai-chat",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);
        Action act = () => factory.CreateClient(null);
        // R-1.5: chat has no modes — a missing chat deployment id must NOT surface
        // as ROUTING_MODE_NOT_FOUND. It's a ROUTING_MODEL_NOT_READY (the model we
        // would need doesn't exist because the caller specified nothing).
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ModelNotReady)
            .Where(ex => ex.ServiceId == "Chat")
            .WithMessage("Chat model id is required. No provider fallback is configured.");
    }

    [TestMethod]
    public void CreateClient_UsesOpenAiResponses_ForOpenAiPlatformResponsesProvider()
    {
        // "openai-responses" targets the OpenAI platform (api.openai.com) via
        // the Responses API. The chat-vs-responses split is independent of
        // the OpenAI-platform-vs-Azure-OpenAI split; both OpenAI-platform
        // variants must produce an OpenAiResponsesClient.
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "o3",
            DisplayName = "O3",
            Provider = "openai-responses",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);
        var client = factory.CreateClient("o3");

        client.Should().BeOfType<OpenAiResponsesClient>();
    }

    [TestMethod]
    public void CreateClient_UsesOpenAiResponses_ForAzureOpenAiResponsesProvider()
    {
        // "azure-openai-responses" also dispatches to the Responses client
        // (the OpenAI SDK handles both platform and Azure endpoints via the
        // same client type; the difference is the config accessor wired at
        // DI time).
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "o3",
            DisplayName = "O3",
            Provider = "azure-openai-responses",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);
        var client = factory.CreateClient("o3");

        client.Should().BeOfType<OpenAiResponsesClient>();
    }

    [TestMethod]
    public void CreateClient_UsesAzureFactory_ForAzureOpenAiChatProvider()
    {
        // "azure-openai-chat" dispatches through the Azure-bound
        // OpenAiChatClientFactory instance. Because OpenAiChatClientFactory
        // is sealed we can't introspect which registered instance was hit
        // from here; that cross-wiring is covered by the keyed-DI smoke test
        // in StartupConfigurationRoutingTests, and by the readiness
        // integration test in RoutingReadinessServiceTests that pins the
        // provider-section mapping.
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "gpt-4.1",
            DisplayName = "GPT 4.1 (Azure)",
            Provider = "azure-openai-chat",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);
        var client = factory.CreateClient("gpt-4.1");

        client.Should().BeOfType<OpenAiChatClient>();
    }

    [TestMethod]
    public void CreateClient_RejectsUnknownProviderStrings()
    {
        // Explicit provider-strings beyond the six the switch knows about
        // must fail-fast with RoutingException, not silently fall through.
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "mystery",
            DisplayName = "Mystery",
            Provider = "openai-legacy-alias-that-no-longer-exists",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);

        Action act = () => factory.CreateClient("mystery");
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ProviderNotReady);
    }

    [TestMethod]
    public void CreateClient_UsesLlamaCpp_ForLlamaProvider()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "qwen3.5-27b",
            DisplayName = "Qwen 3.5 27B",
            Provider = "llama-cpp",
            LocalRuntimeJson = "{\"routerModelId\":\"Qwen3.5-27B-Q6_K\",\"resourceGroupKey\":\"local\",\"runtimeProfileId\":\"qwen3_5\",\"loadParams\":{\"model\":\"Qwen3.5-27B-Q6_K\"}}",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);
        var client = factory.CreateClient("qwen3.5-27b");

        client.Should().BeOfType<LlamaCppChatClient>();
    }

    [TestMethod]
    public async Task CreateClient_UsesParallelToolCallsSetting_FromLocalRuntimeJson()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "qwen3.5-27b",
            DisplayName = "Qwen 3.5 27B",
            Provider = "llama-cpp",
            LocalRuntimeJson = "{\"routerModelId\":\"Qwen3.5-27B-Q6_K\",\"resourceGroupKey\":\"local\",\"runtimeProfileId\":\"qwen3_5\",\"parallelToolCalls\":true}",
            IsActive = true
        });
        db.SaveChanges();

        string? capturedBody = null;
        var httpClient = new HttpClient(new StaticResponseHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var factory = CreateFactory(db);
        var client = factory.CreateClient("qwen3.5-27b", httpClient);

        var paramsSchema = JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"number"}}}""");
        var request = new ChatCompletionRequest(
            messages: [new ChatMessage(AntRunner.Chat.Abstractions.ChatRole.User, "hi")],
            tools: [new ChatToolDefinition(new ChatFunctionDefinition("compute", "compute value", paramsSchema))],
            model: "qwen3.5-27b");

        await client.GetCompletionAsync(request);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("parallel_tool_calls").GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void CreateClient_Throws_ForLlamaModelWithoutLocalRuntimeJson()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "qwen3.5-27b",
            DisplayName = "Qwen 3.5 27B",
            Provider = "llama-cpp",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);

        Action act = () => factory.CreateClient("qwen3.5-27b");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Model 'qwen3.5-27b' is configured as llama-cpp but is missing LocalRuntimeJson.");
    }

    [TestMethod]
    public void CreateClient_Throws_ForUnknownLlamaRuntimeProfile()
    {
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "qwen3.5-27b",
            DisplayName = "Qwen 3.5 27B",
            Provider = "llama-cpp",
            LocalRuntimeJson = "{\"routerModelId\":\"Qwen3.5-27B-Q6_K\",\"resourceGroupKey\":\"local\",\"runtimeProfileId\":\"unknown_profile\"}",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);

        Action act = () => factory.CreateClient("qwen3.5-27b");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unsupported runtimeProfileId*");
    }

    [TestMethod]
    public void CreateClient_Throws_WhenModelNotFound()
    {
        using var db = CreateDb();
        var factory = CreateFactory(db);

        Action act = () => factory.CreateClient("unknown-model");
        // R-1.5: chat is model-driven, not mode-driven. An unknown chat model is
        // ROUTING_MODEL_NOT_READY, never ROUTING_MODE_NOT_FOUND.
        act.Should().Throw<RoutingException>()
            .Where(ex => ex.Code == RoutingErrorCodes.ModelNotReady
                && ex.ModelId == "unknown-model"
                && ex.ServiceId == "Chat")
            .WithMessage("Model 'unknown-model' is not ready: Model not found in the catalog.");
    }

    [TestMethod]
    public void DefaultDeploymentId_IsNull_WhenNoFallbackConfigured()
    {
        using var db = CreateDb();
        var factory = CreateFactory(db);

        factory.DefaultDeploymentId.Should().BeNull();
    }

    [TestMethod]
    public void CreateClient_DoesNotConsultRoutingReadiness_ChatDispatchUsesValidatorOnly()
    {
        // Phase H / R-12.5: chat dispatch MUST NOT call IRoutingReadinessService
        // (which returns ROUTING_RUNTIME_NOT_READY with a RUNTIME_STATE blocker
        // for an in-flight "loading" alias — that behavior is the snapshot/overview
        // semantic). A transient loading state must not fail chat dispatch.
        // Enforced structurally: the factory depends only on IChatTargetResolver
        // and IChatTargetValidator. If someone later reintroduces a readiness
        // probe dependency here, this test's reflection-based guard will fail.
        var constructor = typeof(RoutingChatCompletionClientFactory).GetConstructors().Single();
        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        parameterTypes.Should().NotContain(
            typeof(IRoutingReadinessService),
            "R-12.5: chat dispatch must not call IRoutingReadinessService. Use IChatTargetValidator only.");
    }

    [TestMethod]
    public void CreateClient_Succeeds_WhenValidatorPasses_IndependentOfRuntimeLoadingState()
    {
        // R-12.5 positive case: when IChatTargetValidator.Validate succeeds,
        // dispatch constructs a client even if the underlying llama runtime
        // alias is mid-load. The factory has no runtime-status dependency,
        // so orchestration to wait for the runtime belongs upstream in
        // NotebookModelRuntimeService — not here.
        using var db = CreateDb();
        db.Models.Add(new Model
        {
            ModelId = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet 4.5",
            Provider = "anthropic",
            IsActive = true
        });
        db.SaveChanges();

        var factory = CreateFactory(db);

        Action act = () => factory.CreateClient("claude-sonnet-4-5");
        act.Should().NotThrow("validator.Validate returned success, so dispatch must not consult a separate readiness probe");
    }

    private static RoutingChatCompletionClientFactory CreateFactory(
        ApplicationDbContext db)
    {
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        var openAiConfig = new AzureOpenAiConfig
        {
            ApiKey = "test-key",
            ResourceName = "test-resource",
            ApiVersion = "2025-04-01-preview",
            DeploymentId = "gpt-4.1"
        };

        var anthropicConfig = new AnthropicConfig
        {
            ApiKey = "test-key",
            BaseUrl = "https://example.com/anthropic",
            DefaultModel = "claude-sonnet-4-5",
            DefaultMaxTokens = 16384,
            ThinkingBudgets = new AnthropicThinkingBudgets()
        };

        // In production these four registrations are keyed DI singletons, one
        // per (platform, api) pair. Tests don't use keyed resolution because
        // RoutingChatCompletionClientFactory is constructed positionally, so
        // we just hand it four distinct instances. Using one shared config
        // across all four is fine because these tests don't assert on which
        // credentials flowed through — that's the domain of the integration
        // tests against the running API.
        var openAiPlatformChatFactory = new OpenAiChatClientFactory(httpFactory.Object, openAiConfig);
        var azureOpenAiChatFactory = new OpenAiChatClientFactory(httpFactory.Object, openAiConfig);
        var openAiPlatformResponsesFactory = new OpenAiResponsesClientFactory(httpFactory.Object, openAiConfig);
        var azureOpenAiResponsesFactory = new OpenAiResponsesClientFactory(httpFactory.Object, openAiConfig);
        var anthropicFactory = new AnthropicChatClientFactory(httpFactory.Object, anthropicConfig);
        var llamaCppFactory = new LlamaCppChatClientFactory(
            httpFactory.Object,
            new LlamaCppConfig
            {
                BaseUrl = "http://localhost:8000",
                TimeoutSeconds = 300
            });
        var scopeFactory = new TestServiceScopeFactory(db);

        var runtimeProfileResolver = new Mock<IRuntimeProfileResolver>();
        runtimeProfileResolver
            .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string profileId, CancellationToken _) =>
            {
                if (profileId == "qwen3_5")
                {
                    return new RuntimeProfileData(
                        "qwen3_5",
                        true,
                        @"<think>[\s\S]*?</think>",
                        new Dictionary<string, SamplingParameterDefinition>
                        {
                            ["temperature"] = new("temperature", "Temperature", "Controls randomness", 0, 2, 0.1, 0.7, 0, true)
                        },
                        new ThinkingControl("enabled", new Dictionary<string, IReadOnlyList<ThinkingAction>>
                        {
                            ["none"] = new List<ThinkingAction>
                            {
                                new(ThinkingActionTarget.NestedRequestField, "chat_template_kwargs.enable_thinking", false),
                                new(ThinkingActionTarget.RequestField, "reasoning_format", "none")
                            },
                            ["enabled"] = new List<ThinkingAction>
                            {
                                new(ThinkingActionTarget.NestedRequestField, "chat_template_kwargs.enable_thinking", true)
                            }
                        }));
                }

                throw new InvalidOperationException(
                    $"Unsupported runtimeProfileId '{profileId}'. No runtime profile found in the database.");
            });

        var chatTargetResolver = new ChatTargetResolver(scopeFactory);
        var chatTargetValidator = new Mock<IChatTargetValidator>();
        chatTargetValidator.Setup(v => v.Validate(It.IsAny<ChatTarget>()));

        return new RoutingChatCompletionClientFactory(
            openAiPlatformChatFactory,
            azureOpenAiChatFactory,
            openAiPlatformResponsesFactory,
            azureOpenAiResponsesFactory,
            anthropicFactory,
            llamaCppFactory,
            chatTargetResolver,
            chatTargetValidator.Object,
            runtimeProfileResolver.Object);
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

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responseFactory;

        public StaticResponseHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _responseFactory(request);
        }
    }
}
