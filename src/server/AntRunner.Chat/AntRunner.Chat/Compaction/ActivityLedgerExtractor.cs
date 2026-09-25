using System.Text.Json;

namespace AntRunner.Chat.Compaction;

internal sealed record ActivityLedgerEntry(string ToolName, string? KeyArgument, bool? Succeeded);

internal static class ActivityLedgerExtractor
{
    private const int KeyArgumentMaxChars = 80;

    private static readonly string[] PriorityArgumentKeys =
        { "path", "filePath", "file", "query", "q", "command", "url" };

    public static List<ActivityLedgerEntry> Extract(IReadOnlyList<CompactionBlock> blocks)
    {
        var resultsByCallId = blocks
            .Where(b => b.Kind == CompactionBlockKind.ToolResult && !string.IsNullOrEmpty(b.ToolCallId))
            .GroupBy(b => b.ToolCallId!)
            .ToDictionary(g => g.Key, g => g.First());

        var entries = new List<ActivityLedgerEntry>();
        foreach (var block in blocks)
        {
            if (block.Kind != CompactionBlockKind.ToolCall)
            {
                continue;
            }

            var toolName = string.IsNullOrEmpty(block.ToolName) ? "(unknown tool)" : block.ToolName;
            var keyArgument = ExtractKeyArgument(block.ToolArguments);

            bool? succeeded = null;
            if (block.ToolCallId != null && resultsByCallId.TryGetValue(block.ToolCallId, out var result))
            {
                succeeded = result.ToolSucceeded;
            }

            entries.Add(new ActivityLedgerEntry(toolName, keyArgument, succeeded));
        }

        return entries;
    }

    private static string? ExtractKeyArgument(string? rawArgumentsJson)
    {
        if (string.IsNullOrWhiteSpace(rawArgumentsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CompactionText.Truncate(rawArgumentsJson, KeyArgumentMaxChars);
            }

            foreach (var key in PriorityArgumentKeys)
            {
                if (doc.RootElement.TryGetProperty(key, out var value))
                {
                    return FormatArgumentValue(value);
                }
            }

            using var enumerator = doc.RootElement.EnumerateObject();
            return enumerator.MoveNext() ? FormatArgumentValue(enumerator.Current.Value) : null;
        }
        catch (JsonException)
        {
            return CompactionText.Truncate(rawArgumentsJson, KeyArgumentMaxChars);
        }
    }

    private static string? FormatArgumentValue(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return string.IsNullOrEmpty(text) ? null : CompactionText.Truncate(text, KeyArgumentMaxChars);
    }
}
