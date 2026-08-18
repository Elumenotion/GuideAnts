namespace AntRunner.Chat.Abstractions;

/// <summary>
/// Carries a synthesized partial completion when a provider-specific exception did not include one.
/// </summary>
public sealed class ChatPartialCompletionException : Exception, IChatPartialCompletionException
{
    public ChatPartialCompletionException(
        string terminationStatus,
        ChatCompletionResponse partialResponse,
        Exception? innerException = null)
        : base(innerException?.Message ?? "Chat stream ended with partial output.", innerException)
    {
        TerminationStatus = terminationStatus;
        PartialResponse = partialResponse ?? throw new ArgumentNullException(nameof(partialResponse));
    }

    public ChatCompletionResponse? PartialResponse { get; }

    public string TerminationStatus { get; }
}
