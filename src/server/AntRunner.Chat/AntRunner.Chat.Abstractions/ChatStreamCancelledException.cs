namespace AntRunner.Chat.Abstractions;

/// <summary>
/// Thrown when a streaming completion is cancelled after tokens have already been produced.
/// Carries the partial response so the host can complete the turn with thinking, text, and usage.
/// </summary>
public sealed class ChatStreamCancelledException : OperationCanceledException
{
    public ChatCompletionResponse PartialResponse { get; }

    public ChatStreamCancelledException(
        ChatCompletionResponse partialResponse,
        CancellationToken cancellationToken = default)
        : base("Chat stream was cancelled.", cancellationToken)
    {
        PartialResponse = partialResponse ?? throw new ArgumentNullException(nameof(partialResponse));
    }
}
