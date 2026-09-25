using AntRunner.Chat.Abstractions;
using AntRunner.Chat.LlamaCpp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

/// <summary>
/// Same host as <see cref="TestWebApplicationFactory"/> except chat completions go to a real llama-server,
/// through the production <see cref="LlamaCppChatClient"/> (so real overflow bodies get classified).
/// </summary>
internal sealed class LlamaCompactionWebApplicationFactory : TestWebApplicationFactory
{
    public RecordingChatCompletionClient Recorder { get; }

    public LlamaCompactionWebApplicationFactory(string baseUrl, string modelId)
    {
        var inner = new LlamaCppChatClient(
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
            new LlamaCppConfig { BaseUrl = baseUrl, TimeoutSeconds = 300 },
            modelId);
        Recorder = new RecordingChatCompletionClient(inner);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatCompletionClientFactory>();
            services.AddSingleton<IChatCompletionClientFactory>(new SingleClientFactory(Recorder));
        });
    }

    private sealed class SingleClientFactory(IChatCompletionClient client) : IChatCompletionClientFactory
    {
        public string? DefaultDeploymentId => "llama-e2e";
        public IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null) => client;
    }
}

/// <summary>Delegating client that remembers the last request, so tests can see what the model was offered.</summary>
internal sealed class RecordingChatCompletionClient(IChatCompletionClient inner) : IChatCompletionClient
{
    public IReadOnlyList<ChatMessage>? LastRequestMessages { get; private set; }
    public IReadOnlyList<string>? LastRequestToolNames { get; private set; }

    public bool SupportsToolChoiceNone => inner.SupportsToolChoiceNone;

    public Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Record(request);
        return inner.GetCompletionAsync(request, cancellationToken);
    }

    public Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request, Action<ChatCompletionChunk> onChunk, CancellationToken cancellationToken = default)
    {
        Record(request);
        return inner.StreamCompletionAsync(request, onChunk, cancellationToken);
    }

    private void Record(ChatCompletionRequest request)
    {
        LastRequestMessages = request.Messages.ToList();
        LastRequestToolNames = (request.Tools ?? []).Select(t => t.Function?.Name ?? string.Empty).ToList();
    }
}
