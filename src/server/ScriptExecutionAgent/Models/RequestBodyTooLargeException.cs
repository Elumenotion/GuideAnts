namespace ScriptExecutionAgent;

/// <summary>
/// Thrown by <see cref="AgentRequestBody"/> when a request body exceeds its route's byte cap.
/// Mapped to a plain-text 413 response by the endpoint error mapping so the agent's
/// text/error contract is preserved.
/// </summary>
internal sealed class RequestBodyTooLargeException : Exception
{
    public RequestBodyTooLargeException(long maxBytes)
        : base($"request body exceeds the limit of {maxBytes} bytes")
    {
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
}
