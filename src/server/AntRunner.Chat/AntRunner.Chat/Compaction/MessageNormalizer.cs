using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling.Functions;

namespace AntRunner.Chat.Compaction;

internal static class MessageNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static List<CompactionBlock> Normalize(IReadOnlyList<ChatMessage> messages)
    {
        var blocks = new List<CompactionBlock>();
        foreach (var message in messages)
        {
            if (message is null)
            {
                continue;
            }

            switch (message.Role)
            {
                case ChatRole.User:
                    blocks.Add(new CompactionBlock(CompactionBlockKind.UserMessage, message.GetText()));
                    break;

                case ChatRole.System:
                case ChatRole.Developer:
                    blocks.Add(new CompactionBlock(CompactionBlockKind.SystemMessage, message.GetText()));
                    break;

                case ChatRole.Assistant:
                    NormalizeAssistantMessage(message, blocks);
                    break;

                case ChatRole.Tool:
                    NormalizeToolResultMessage(message, blocks);
                    break;
            }
        }

        return blocks;
    }

    private static void NormalizeAssistantMessage(ChatMessage message, List<CompactionBlock> blocks)
    {
        var text = message.GetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            blocks.Add(new CompactionBlock(CompactionBlockKind.AssistantMessage, text));
        }

        if (message.ToolCalls == null)
        {
            return;
        }

        foreach (var call in message.ToolCalls)
        {
            if (!call.IsFunction)
            {
                continue;
            }

            blocks.Add(new CompactionBlock(
                CompactionBlockKind.ToolCall,
                Text: string.Empty,
                ToolCallId: call.Id,
                ToolName: call.Function.Name,
                ToolArguments: call.Function.Arguments.ValueKind == JsonValueKind.Undefined
                    ? null
                    : call.Function.Arguments.GetRawText()));
        }
    }

    private static void NormalizeToolResultMessage(ChatMessage message, List<CompactionBlock> blocks)
    {
        var text = message.GetText();
        blocks.Add(new CompactionBlock(
            CompactionBlockKind.ToolResult,
            text,
            ToolCallId: message.ToolCallId,
            ToolName: message.FunctionName,
            ToolSucceeded: TryGetToolSuccess(text)));
    }

    // ScriptExecutionResult.ExitCode is the only protocol-guaranteed success signal (D2: no
    // content heuristics). Any other JSON-or-not shape is neither ok nor error - "unknown".
    private static bool? TryGetToolSuccess(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ScriptExecutionResult>(text, JsonOptions);
            return parsed?.ExitCode is int code ? code == 0 : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
