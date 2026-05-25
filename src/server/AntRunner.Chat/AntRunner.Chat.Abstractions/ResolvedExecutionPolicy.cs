using System.Text.Json;

namespace AntRunner.Chat.Abstractions;

public enum ParameterAuthority
{
    AssistantDefinition,
    GlobalOverride
}

public sealed record ResolvedExecutionPolicy(
    string ModelId,
    string Provider,
    ParameterAuthority Authority,
    IReadOnlyDictionary<string, JsonElement> Parameters);
