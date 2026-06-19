namespace AntRunner.Chat.Abstractions;

/// <summary>
/// Thrown when a chat provider rejects a request because the assembled prompt exceeds the model's
/// context window (e.g. llama.cpp's <c>exceed_context_size_error</c>). Unlike a transport failure
/// or a runtime crash this is a recoverable, request-shaping problem: the execution engine can
/// unwind the oversized message from the turn and retry with a smaller request.
/// </summary>
public sealed class ChatContextOverflowException : Exception
{
    /// <summary>Prompt token count reported by the provider, when available.</summary>
    public int? PromptTokens { get; }

    /// <summary>Context window size reported by the provider, when available.</summary>
    public int? ContextSize { get; }

    /// <summary>Short, user-safe excerpt from the provider error body.</summary>
    public string? UpstreamDetail { get; }

    public ChatContextOverflowException(
        string message,
        int? promptTokens = null,
        int? contextSize = null,
        string? upstreamDetail = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        PromptTokens = promptTokens;
        ContextSize = contextSize;
        UpstreamDetail = upstreamDetail;
    }
}
