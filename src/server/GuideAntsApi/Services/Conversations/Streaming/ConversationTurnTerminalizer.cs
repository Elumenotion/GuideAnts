using System.Text;
using System.Text.Json;
using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.LlamaCpp;
using GuideAntsApi.Services.Conversations.Persistence;

namespace GuideAntsApi.Services.Conversations.Streaming;

public static class ConversationTurnTerminalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static TerminalizeTurnRequest BuildRequest(
        ConversationStreamRunContext context,
        string terminalStatus,
        ChatRunOutput? output,
        Guid? currentAssistantMessageId,
        StringBuilder currentAssistantContent,
        StringBuilder? currentThinkingContent,
        IReadOnlyList<Guid> assistantMessageIds,
        string? terminationCode = null,
        string? terminationDetail = null,
        bool pruneIncompleteToolCalls = false)
    {
        var snapshots = new List<TerminalizeAssistantSnapshot>();
        if (currentAssistantMessageId.HasValue)
        {
            string? thinkingJson = null;
            if (currentThinkingContent is { Length: > 0 })
            {
                thinkingJson = JsonSerializer.Serialize(
                    new[] { ChatThinkingBlock.ForThinking(currentThinkingContent.ToString(), string.Empty) },
                    JsonOptions);
            }

            snapshots.Add(new TerminalizeAssistantSnapshot(
                currentAssistantMessageId.Value,
                currentAssistantContent.ToString(),
                ThinkingBlocksJson: thinkingJson));
        }

        return new TerminalizeTurnRequest(
            TurnId: context.DbTurn.Id,
            ConversationId: context.Conversation.Id,
            TurnIndex: context.TurnIndex,
            TerminalStatus: terminalStatus,
            TerminationCode: terminationCode,
            TerminationDetail: terminationDetail,
            ExecutionId: context.DbTurn.ExecutionId,
            Output: output,
            AssistantSnapshots: snapshots.Count > 0 ? snapshots : null,
            PruneIncompleteToolCalls: pruneIncompleteToolCalls,
            AssistantMessageIdsForThinking: assistantMessageIds.Count > 0 ? assistantMessageIds : null);
    }

    public static string? MapTerminationCode(Exception ex)
    {
        var inner = ex is ChatConversationException chatEx ? chatEx.InnerException : ex.InnerException;
        var display = ex is ChatConversationException && inner != null ? inner : ex;

        if (display is LlamaInferenceTimeoutException)
        {
            return "local_llm_timeout";
        }

        if (display is LlamaRuntimeCrashedException crash)
        {
            return crash.Reason switch
            {
                LlamaRuntimeCrashReason.OutOfMemory => "local_llm_oom",
                LlamaRuntimeCrashReason.NotReady => "local_llm_not_ready",
                LlamaRuntimeCrashReason.Recovering => "local_llm_recovering",
                _ => "local_llm_crashed"
            };
        }

        if (display is ChatContextOverflowException)
        {
            return "chat_context_overflow";
        }

        return null;
    }

    public static string MapTerminalStatus(ChatRunOutput? output, Exception? ex)
    {
        if (output?.Status != null)
        {
            var status = output.Status;
            if (string.Equals(status, "timed_out", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "pending_client_tool", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }
        }

        if (ex is OperationCanceledException || ex is ChatRunCancelledException)
        {
            return "cancelled";
        }

        if (ex is LlamaInferenceTimeoutException
            || ex?.InnerException is LlamaInferenceTimeoutException)
        {
            return "timed_out";
        }

        return "failed";
    }
}
