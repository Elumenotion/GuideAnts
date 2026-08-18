namespace AntRunner.Chat.Abstractions;

/// <summary>
/// Marks an inference failure that may include a partial completion response.
/// </summary>
public interface IChatPartialCompletionException
{
    ChatCompletionResponse? PartialResponse { get; }

    /// <summary>
    /// Terminal run status to persist (e.g. timed_out, failed).
    /// </summary>
    string TerminationStatus { get; }
}
