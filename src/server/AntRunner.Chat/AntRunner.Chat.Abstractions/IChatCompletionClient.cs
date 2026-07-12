namespace AntRunner.Chat.Abstractions;

public interface IChatCompletionClient
{
    /// <summary>
    /// Whether this client can honor <c>tool_choice: "none"</c> while keeping the tools array declared.
    /// </summary>
    bool SupportsToolChoiceNone { get; }

    Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);

    Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default);
}
