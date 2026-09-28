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
public sealed class ThreadRunContextOverflowTests
{
    [TestInitialize]
    public void SetUp() => AssistantUtility.ClearAllCache();

    [TestCleanup]
    public void TearDown() => AssistantUtility.ClearAllCache();

    [TestMethod]
    public async Task ExecuteAsync_ContextOverflow_PropagatesImmediately_WithNoMessageContentMutated()
    {
        const string assistantName = "Overflow Test Assistant";
        SeedAssistantCache(assistantName, new AssistantDefinition { Name = assistantName, Model = "gpt-4o-mini" });

        var client = new ThrowingClient();
        var factory = new SingleClientFactory(client);

        var options = new ChatRunOptions
        {
            AssistantName = assistantName,
            Instructions = "original-instructions-marker",
            ExecutionPolicy = new ResolvedExecutionPolicy(
                "gpt-4o-mini",
                "openai-chat",
                ParameterAuthority.AssistantDefinition,
                new Dictionary<string, JsonElement>())
        };
        var ctx = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var act = () => ThreadRun.ExecuteAsync(
            options,
            factory,
            previous: null,
            httpClient: null,
            onMessage: null,
            onStream: null,
            ctx,
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ChatConversationException>();
        ex.And.InnerException.Should().BeOfType<ChatContextOverflowException>();

        // D5: overflow fails cleanly. No unwind, no retry -- exactly one call, ever.
        client.CallCount.Should().Be(1,
            "the exception must propagate on the very first overflow, not after an unwind-and-retry");

        client.CapturedRequests.Should().ContainSingle();
        var requestText = string.Join(" ", client.CapturedRequests[0].Messages.Select(m => m.GetText()));
        requestText.Should().Contain("original-instructions-marker",
            "the original message content must survive untouched");
        requestText.Should().NotContain("aborted due to size restrictions",
            "no message may ever be replaced with an abort notice");
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

    private sealed class ThrowingClient : IChatCompletionClient
    {
        public bool SupportsToolChoiceNone => true;
        public int CallCount { get; private set; }
        public List<ChatCompletionRequest> CapturedRequests { get; } = [];

        public Task<ChatCompletionResponse> GetCompletionAsync(
            ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            CapturedRequests.Add(request);
            throw new ChatContextOverflowException(
                "The request exceeded the model's context window.",
                promptTokens: 9001,
                contextSize: 4096);
        }

        public Task<ChatCompletionResponse> StreamCompletionAsync(
            ChatCompletionRequest request,
            Action<ChatCompletionChunk> onChunk,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Test uses the non-streaming path only.");
    }

    private sealed class SingleClientFactory : IChatCompletionClientFactory
    {
        private readonly IChatCompletionClient _client;
        public SingleClientFactory(IChatCompletionClient client) => _client = client;
        public string? DefaultDeploymentId => "gpt-4o-mini";
        public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null) => _client;
    }
}
