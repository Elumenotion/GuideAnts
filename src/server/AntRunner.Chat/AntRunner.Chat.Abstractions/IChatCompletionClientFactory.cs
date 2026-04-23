namespace AntRunner.Chat.Abstractions;

public interface IChatCompletionClientFactory
{
    IChatCompletionClient CreateClient(string? deploymentId, HttpClient? httpClient = null);

    string? DefaultDeploymentId { get; }
}
