using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

internal sealed class FakeChatCompletionClientFactory : IChatCompletionClientFactory
{
    public string? DefaultDeploymentId => "test-deployment";

    public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null)
    {
        return new FakeChatCompletionClient();
    }
}

internal sealed class FakeChatCompletionClient : IChatCompletionClient
{
    public Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CreateResponse());
    }

    public Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        onChunk(new ChatCompletionChunk(
            [
                new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, "Test assistant response."), null)
            ]));

        return Task.FromResult(CreateResponse());
    }

    private static ChatCompletionResponse CreateResponse()
    {
        return new ChatCompletionResponse(
            [
                new ChatChoice(
                    new ChatMessage(ChatRole.Assistant, "Test assistant response."),
                    "stop")
            ],
            new ChatCompletionUsage
            {
                PromptTokens = 1,
                CompletionTokens = 1,
                TotalTokens = 2
            });
    }
}
