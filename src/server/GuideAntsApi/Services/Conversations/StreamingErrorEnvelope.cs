using AntRunner.Chat;
using AntRunner.Chat.LlamaCpp;
using GuideAntsApi.Services.Routing;
using System.Net;

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
        var display = ex is ChatConversationException && inner != null ? inner : ex;

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

        var routing = ex as RoutingException ?? inner as RoutingException;
        if (routing != null)
        {
            return new
            {
                code = routing.Code,
                message = routing.Message,
                type = nameof(RoutingException),
                action = routing.Action,
                blockers = routing.Blockers,
                serviceId = routing.ServiceId,
                modeId = routing.ModeId,
                providerSection = routing.ProviderSection,
                modelId = routing.ModelId,
                innerMessage = inner != null && !ReferenceEquals(inner, routing) ? inner.Message : null,
                innerType = inner != null && !ReferenceEquals(inner, routing) ? inner.GetType().Name : null,
                timestamp = DateTime.UtcNow
            };
        }

        var http = display as HttpRequestException;
        var statusCode = http?.StatusCode.HasValue == true ? (int?)http.StatusCode.Value : null;

        return new
        {
            code = (string?)null,
            message = BuildDisplayMessage(display),
            type = display.GetType().Name,
            innerMessage = inner != null && !ReferenceEquals(display, inner) ? inner.Message : null,
            innerType = inner != null && !ReferenceEquals(display, inner) ? inner.GetType().Name : null,
            statusCode,
            action = BuildAction(display, statusCode),
            timestamp = DateTime.UtcNow
        };
    }

    private static string BuildDisplayMessage(Exception ex)
    {
        var message = ex.Message?.Trim();
        return string.IsNullOrWhiteSpace(message)
            ? "Chat run failed."
            : message;
    }

    private static string? BuildAction(Exception ex, int? statusCode)
    {
        if (statusCode is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            return "Open Settings → Connections and verify the API key or credentials for the selected chat model provider.";
        }

        var message = ex.Message;
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase)
            || message.Contains("403", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return "Open Settings → Connections and verify the API key or credentials for the selected chat model provider.";
        }

        return null;
    }
}
