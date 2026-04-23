using AntRunner.Chat;
using AntRunner.Chat.LlamaCpp;

namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Shared shape for SSE <c>error</c> events. Centralizing the runtime classification here
/// keeps <see cref="ConversationService"/> and <see cref="PublishedConversationService"/>
/// from diverging on how they surface <see cref="LlamaRuntimeCrashedException"/> to the
/// browser — the client uses the <c>code</c> field to decide whether to render the crash
/// recovery modal, drive the load dialog, or fall through to the generic error toast.
///
/// Codes emitted (keep in sync with useStreamingEventHandler.ts):
///   local_llm_oom       — CUDA/allocator OOM; crash modal -> restart -> load
///   local_llm_crashed   — 5xx or mid-stream drop; crash modal -> restart -> load
///   local_llm_not_ready — runtime up but no model loaded; straight to load dialog
/// </summary>
internal static class StreamingErrorEnvelope
{
    public static object Build(Exception ex)
    {
        var inner = ex is ChatConversationException chatEx ? chatEx.InnerException : ex.InnerException;

        // Either layer may throw the runtime exception directly, or wrap it inside a
        // ChatConversationException. Handle both without the caller caring.
        var crash = ex as LlamaRuntimeCrashedException
                    ?? inner as LlamaRuntimeCrashedException;

        if (crash != null)
        {
            var code = crash.Reason switch
            {
                LlamaRuntimeCrashReason.OutOfMemory => "local_llm_oom",
                LlamaRuntimeCrashReason.NotReady => "local_llm_not_ready",
                _ => "local_llm_crashed"
            };

            return new
            {
                code,
                reason = crash.Reason.ToString(),
                message = crash.Message,
                type = nameof(LlamaRuntimeCrashedException),
                innerMessage = crash.UpstreamDetail,
                innerType = crash.InnerException?.GetType().Name,
                statusCode = crash.StatusCode.HasValue ? (int?)crash.StatusCode.Value : null,
                timestamp = DateTime.UtcNow
            };
        }

        return new
        {
            code = (string?)null,
            message = ex.Message,
            type = ex.GetType().Name,
            innerMessage = inner?.Message,
            innerType = inner?.GetType().Name,
            timestamp = DateTime.UtcNow
        };
    }
}
