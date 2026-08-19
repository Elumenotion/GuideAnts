using AntRunner.Chat.Abstractions;

namespace AntRunner.Chat;

public enum ContextOverflowUnwindPhase
{
    None = 0,
    ToolPair = 1,
    Thinking = 2,
    OldestNonSystem = 3
}

public sealed class ContextOverflowUnwindResult
{
    public bool DidUnwind { get; init; }
    public ContextOverflowUnwindPhase Phase { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<ChatMessage> RemovedMessages { get; init; } = [];
    public ChatMessage? ThinkingStrippedFrom { get; init; }

    public static ContextOverflowUnwindResult None { get; } = new()
    {
        DidUnwind = false,
        Phase = ContextOverflowUnwindPhase.None
    };
}

/// <summary>
/// One-step eviction when a chat completion is rejected for context overflow.
/// Prefer oldest tool-call pairs (assistant call + all results, removed), then largest thinking
/// (stripped in place), then oldest remaining non-system messages. The latest user message is kept.
/// </summary>
public static class ContextOverflowUnwinder
{
    public const string EvictionNoticePrefix = "[Context eviction:";

    public const string ToolPairEvictionNotice =
        "[Context eviction: tool call pair removed to fit the model context window.]";

    public const string MessageEvictionNotice =
        "[Context eviction: message removed to fit the model context window.]";

    public static bool IsEvictionNotice(string? content) =>
        !string.IsNullOrEmpty(content)
        && (content.StartsWith(EvictionNoticePrefix, StringComparison.Ordinal)
            || content.StartsWith("[Message aborted due to size", StringComparison.Ordinal));

    public static bool TryUnwind(List<ChatMessage> messages, out ContextOverflowUnwindResult result)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (TryEvictOldestToolPair(messages, out result)
            || TryStripLargestThinking(messages, out result)
            || TryEvictOldestNonSystem(messages, out result))
        {
            return true;
        }

        result = ContextOverflowUnwindResult.None;
        return false;
    }

    private static bool TryEvictOldestToolPair(List<ChatMessage> messages, out ContextOverflowUnwindResult result)
    {
        var lastUserIndex = LastUserIndex(messages);

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (IsProtected(message, i, lastUserIndex))
            {
                continue;
            }

            if (message.Role != ChatRole.Assistant || message.ToolCalls is not { Count: > 0 })
            {
                continue;
            }

            var callIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in message.ToolCalls)
            {
                if (!string.IsNullOrEmpty(call.Id))
                {
                    callIds.Add(call.Id);
                }
            }

            if (callIds.Count == 0)
            {
                continue;
            }

            var removeIndices = new List<int> { i };
            for (var j = 0; j < messages.Count; j++)
            {
                if (j == i)
                {
                    continue;
                }

                var other = messages[j];
                if (other.Role == ChatRole.Tool
                    && !string.IsNullOrEmpty(other.ToolCallId)
                    && callIds.Contains(other.ToolCallId))
                {
                    removeIndices.Add(j);
                }
            }

            result = RemoveIndices(
                messages,
                removeIndices,
                ContextOverflowUnwindPhase.ToolPair,
                $"Evicted oldest tool-call pair at index {i} ({callIds.Count} call(s), {removeIndices.Count} message(s)).");
            return true;
        }

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (IsProtected(message, i, lastUserIndex) || message.Role != ChatRole.Tool)
            {
                continue;
            }

            result = RemoveIndices(
                messages,
                [i],
                ContextOverflowUnwindPhase.ToolPair,
                $"Evicted oldest orphan tool result at index {i}.");
            return true;
        }

        result = ContextOverflowUnwindResult.None;
        return false;
    }

    private static bool TryStripLargestThinking(List<ChatMessage> messages, out ContextOverflowUnwindResult result)
    {
        var bestIndex = -1;
        var bestSize = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var size = ThinkingSize(messages[i]);
            if (size > bestSize)
            {
                bestSize = size;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            result = ContextOverflowUnwindResult.None;
            return false;
        }

        var original = messages[bestIndex];
        var stripped = new ChatMessage(original.Role, original.Content, original.ToolCalls, thinkingBlocks: null);
        messages[bestIndex] = stripped;

        result = new ContextOverflowUnwindResult
        {
            DidUnwind = true,
            Phase = ContextOverflowUnwindPhase.Thinking,
            Description = $"Stripped thinking ({bestSize} chars) from assistant message at index {bestIndex}.",
            ThinkingStrippedFrom = stripped
        };
        return true;
    }

    private static bool TryEvictOldestNonSystem(List<ChatMessage> messages, out ContextOverflowUnwindResult result)
    {
        var lastUserIndex = LastUserIndex(messages);
        for (var i = 0; i < messages.Count; i++)
        {
            if (IsProtected(messages[i], i, lastUserIndex))
            {
                continue;
            }

            result = RemoveIndices(
                messages,
                [i],
                ContextOverflowUnwindPhase.OldestNonSystem,
                $"Evicted oldest remaining non-system message at index {i} ({messages[i].Role}).");
            return true;
        }

        result = ContextOverflowUnwindResult.None;
        return false;
    }

    private static ContextOverflowUnwindResult RemoveIndices(
        List<ChatMessage> messages,
        List<int> indices,
        ContextOverflowUnwindPhase phase,
        string description)
    {
        var unique = indices.Distinct().OrderByDescending(x => x).ToList();
        var removed = new List<ChatMessage>(unique.Count);
        foreach (var index in unique)
        {
            if (index < 0 || index >= messages.Count)
            {
                continue;
            }

            removed.Add(messages[index]);
            messages.RemoveAt(index);
        }

        removed.Reverse();
        return new ContextOverflowUnwindResult
        {
            DidUnwind = removed.Count > 0,
            Phase = phase,
            Description = description,
            RemovedMessages = removed
        };
    }

    private static bool IsProtected(ChatMessage message, int index, int lastUserIndex)
    {
        if (message.Role is ChatRole.System or ChatRole.Developer)
        {
            return true;
        }

        return lastUserIndex >= 0 && index == lastUserIndex;
    }

    private static int LastUserIndex(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                return i;
            }
        }

        return -1;
    }

    private static int ThinkingSize(ChatMessage message)
    {
        if (message.ThinkingBlocks is not { Count: > 0 } blocks)
        {
            return 0;
        }

        var size = 0;
        foreach (var block in blocks)
        {
            size += block.Thinking?.Length ?? 0;
            size += block.Data?.Length ?? 0;
        }

        return size;
    }
}
