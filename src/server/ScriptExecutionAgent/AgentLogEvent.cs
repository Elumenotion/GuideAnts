using System.Text.Json;

namespace ScriptExecutionAgent;

internal static class AgentLogEvent
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    public static void Emit(string eventName, IReadOnlyDictionary<string, object?> fields)
    {
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event"] = eventName,
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
        };

        foreach (var pair in fields)
        {
            payload[pair.Key] = pair.Value;
        }

        Console.WriteLine(JsonSerializer.Serialize(payload, SerializerOptions));
        Console.Out.Flush();
    }
}
