using System.Collections;
using System.Reflection;
using System.Text.Json;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
[DoNotParallelize]
public sealed class ConversationRecallExposureTests
{
    [TestInitialize]
    public void SetUp()
    {
        AssistantUtility.ClearAllCache();
        ToolContractRegistry.RefreshContracts();
    }

    [TestCleanup]
    public void TearDown() => AssistantUtility.ClearAllCache();

    [TestMethod]
    public async Task ExecuteAsync_FlagOff_DoesNotAdvertiseConversationRecall()
    {
        var request = await RunOnceAsync(enableConversationRecall: false);

        ToolNames(request).Should().NotContain("conversation_recall",
            "D7: a conversation that was never compacted must see zero behavior change");
    }

    [TestMethod]
    public async Task ExecuteAsync_FlagOn_AdvertisesConversationRecallExactlyOnce()
    {
        var request = await RunOnceAsync(enableConversationRecall: true);

        ToolNames(request).Should().ContainSingle(n => n == "conversation_recall");
    }

    [TestMethod]
    public async Task ExecuteAsync_FlagOn_AdvertisedSchemaExposesOnlyQueryAndPage()
    {
        var request = await RunOnceAsync(enableConversationRecall: true);

        var recall = request.Tools!.Single(t => t.Function?.Name == "conversation_recall");
        var parameters = recall.Function!.Parameters!.ToJsonString();

        parameters.Should().Contain("query");
        parameters.Should().Contain("page");
        parameters.Should().NotContain("context");
        parameters.Should().NotContain("conversationId");
    }

    private static IEnumerable<string> ToolNames(ChatCompletionRequest request) =>
        (request.Tools ?? []).Select(t => t.Function?.Name ?? string.Empty);

    private static async Task<ChatCompletionRequest> RunOnceAsync(bool enableConversationRecall)
    {
        const string assistantName = "Recall Exposure Test Assistant";
        SeedAssistantCache(assistantName, new AssistantDefinition { Name = assistantName, Model = "gpt-4o-mini" });

        var client = new StoppingClient();
        var options = new ChatRunOptions
        {
            AssistantName = assistantName,
            Instructions = "hello",
            EnableConversationRecall = enableConversationRecall,
            ExecutionPolicy = new ResolvedExecutionPolicy(
                "gpt-4o-mini",
                "openai-chat",
                ParameterAuthority.AssistantDefinition,
                new Dictionary<string, JsonElement>())
        };

        await ThreadRun.ExecuteAsync(
            options,
            new SingleClientFactory(client),
            previous: null,
            httpClient: null,
            onMessage: null,
            onStream: null,
            new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        client.CapturedRequests.Should().NotBeEmpty();
        return client.CapturedRequests[0];
    }

    private static void SeedAssistantCache(string name, AssistantDefinition definition)
    {
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(cacheType, definition)!;
        var cache = (IDictionary)typeof(AssistantUtility)
            .GetField("AssistantDefinitionCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        cache[name] = entry;
    }

    private sealed class StoppingClient : IChatCompletionClient
    {
        public bool SupportsToolChoiceNone => true;
        public List<ChatCompletionRequest> CapturedRequests { get; } = [];

        public Task<ChatCompletionResponse> GetCompletionAsync(
            ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequests.Add(request);
            return Task.FromResult(new ChatCompletionResponse(
                new[] { new ChatChoice(new ChatMessage(ChatRole.Assistant, "done"), "stop") }, null));
        }

        public Task<ChatCompletionResponse> StreamCompletionAsync(
            ChatCompletionRequest request,
            Action<ChatCompletionChunk> onChunk,
            CancellationToken cancellationToken = default) =>
            GetCompletionAsync(request, cancellationToken);
    }

    private sealed class SingleClientFactory : IChatCompletionClientFactory
    {
        private readonly IChatCompletionClient _client;
        public SingleClientFactory(IChatCompletionClient client) => _client = client;
        public string? DefaultDeploymentId => "gpt-4o-mini";
        public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null) => _client;
    }
}
