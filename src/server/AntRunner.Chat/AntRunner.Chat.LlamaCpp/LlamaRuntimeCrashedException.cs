using System.Net;

namespace AntRunner.Chat.LlamaCpp;

/// <summary>
/// Reason a llama.cpp request failed in a way that indicates the local runtime cannot currently
/// service the request. The <see cref="LlamaCppChatClient"/> uses this to classify responses so
/// the API/UI can route each case to the correct recovery surface:
///
///   OutOfMemory -> crash modal (restart + reload, warn operator the last model was too big)
///   Crashed     -> crash modal (restart + reload)
///   NotReady    -> load modal   (no restart needed — just load a model)
///
/// Keeping all three on one exception/enum lets the SSE envelope and client share one branching
/// surface (<c>code</c> field) instead of spawning a second "service unavailable" exception type.
/// </summary>
public enum LlamaRuntimeCrashReason
{
    /// <summary>Response body matched one of the known CUDA / allocator out-of-memory markers.</summary>
    OutOfMemory,

    /// <summary>5xx without an OOM marker, or mid-stream connection drop.</summary>
    Crashed,

    /// <summary>
    /// The runtime is up but has no model loaded (llama-server returns 400 with a
    /// "the server does not have a model loaded" error). The UI should prompt the user to
    /// load a model — a restart is not required and would be wasteful.
    /// </summary>
    NotReady
}

public sealed class LlamaRuntimeCrashedException : Exception
{
    public LlamaRuntimeCrashReason Reason { get; }
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Short, user-safe detail extracted from the server response (capped length, no giant base64 bodies).
    /// Full request/response diagnostics are always logged separately via ILogger.
    /// </summary>
    public string? UpstreamDetail { get; }

    public LlamaRuntimeCrashedException(
        LlamaRuntimeCrashReason reason,
        string message,
        HttpStatusCode? statusCode,
        string? upstreamDetail,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        StatusCode = statusCode;
        UpstreamDetail = upstreamDetail;
    }
}
