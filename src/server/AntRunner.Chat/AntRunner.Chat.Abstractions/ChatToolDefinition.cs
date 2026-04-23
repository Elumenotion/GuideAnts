using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AntRunner.Chat.Abstractions;

public sealed class ChatToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("function")]
    public ChatFunctionDefinition? Function { get; }

    public ChatToolDefinition(ChatFunctionDefinition function)
    {
        Type = "function";
        Function = function;
    }
}

public sealed class ChatFunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("description")]
    public string? Description { get; }

    [JsonPropertyName("parameters")]
    public JsonNode? Parameters { get; }

    public ChatFunctionDefinition(string name, string? description, JsonNode? parameters)
    {
        Name = name;
        Description = description;
        Parameters = parameters;
    }
}
