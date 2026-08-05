using System.Text.Json;
using System.Text.Json.Nodes;

namespace AntRunner.Chat.Abstractions;

/// <summary>
/// Where a <see cref="ProviderChatBehaviorAction"/> writes its value.
/// Mirrors the llama.cpp thinking-control targets so a catalog row means the same
/// thing on every provider that honors model-owned chat behavior.
/// </summary>
public enum ProviderChatBehaviorActionTarget
{
    /// <summary>Top-level request body field, e.g. <c>reasoning_effort</c>.</summary>
    RequestField,

    /// <summary>Dotted path into a nested object, e.g. <c>chat_template_kwargs.enable_thinking</c>.</summary>
    NestedRequestField,

    /// <summary>Text prepended to the first system message (or a new one when absent).</summary>
    SystemMessagePrefix
}

public sealed record ProviderChatBehaviorAction(
    ProviderChatBehaviorActionTarget Target,
    string Key,
    object? Value);

public sealed record ProviderThinkingControl(
    string? DefaultChoice,
    IReadOnlyDictionary<string, IReadOnlyList<ProviderChatBehaviorAction>> ChoiceActions);

/// <summary>
/// Model-row owned request shaping for OpenAI-compatible providers that do not
/// normalize reasoning themselves (Hugging Face router) or expose vendor-specific
/// body fields the typed request shape does not carry (OpenRouter <c>provider</c>,
/// <c>parallel_tool_calls</c>, …).
/// </summary>
public sealed record ProviderChatBehavior(
    ProviderThinkingControl? ThinkingControl = null,
    IReadOnlyDictionary<string, JsonElement>? ExtraRequestFields = null)
{
    public bool HasExtraRequestFields => ExtraRequestFields is { Count: > 0 };

    public bool HasThinkingControl => ThinkingControl?.ChoiceActions is { Count: > 0 };
}

/// <summary>
/// Applies <see cref="ProviderChatBehavior"/> to an already-serialized request body.
/// Provider clients keep their typed payload records and hand the serialized object
/// here so row-owned fields can override or extend anything in the body.
/// </summary>
public static class ProviderChatBehaviorApplier
{
    /// <summary>
    /// Merges extra request fields into <paramref name="body"/>. Row-owned keys win over
    /// whatever the typed payload produced.
    /// </summary>
    public static void ApplyExtraRequestFields(JsonObject body, ProviderChatBehavior? behavior)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (behavior?.ExtraRequestFields is not { Count: > 0 } fields)
        {
            return;
        }

        foreach (var (key, value) in fields)
        {
            body[key] = JsonNode.Parse(value.GetRawText());
        }
    }

    /// <summary>
    /// Resolves the thinking-control actions for <paramref name="reasoningEffort"/>, falling back to
    /// the configured default choice. Returns <c>null</c> when the row configures nothing for that
    /// choice, which leaves the caller's built-in reasoning mapping in place — non-local rows list
    /// their reasoning choices separately and may offer choices the control does not cover.
    /// </summary>
    public static IReadOnlyList<ProviderChatBehaviorAction>? ResolveThinkingActions(
        ProviderChatBehavior? behavior,
        string? reasoningEffort)
    {
        if (behavior?.ThinkingControl?.ChoiceActions is not { Count: > 0 } choiceActions)
        {
            return null;
        }

        var choice = string.IsNullOrWhiteSpace(reasoningEffort)
            ? behavior.ThinkingControl.DefaultChoice
            : reasoningEffort;
        if (string.IsNullOrWhiteSpace(choice))
        {
            return null;
        }

        var matchingKey = choiceActions.Keys
            .FirstOrDefault(key => string.Equals(key, choice, StringComparison.OrdinalIgnoreCase));
        return matchingKey != null && choiceActions.TryGetValue(matchingKey, out var actions)
            ? actions
            : null;
    }

    /// <summary>
    /// Writes resolved thinking-control actions into the request body. The caller is expected to
    /// clear its own reasoning fields first so a configured row is the single authority.
    /// </summary>
    public static void ApplyThinkingActions(
        JsonObject body,
        JsonArray? messages,
        IReadOnlyList<ProviderChatBehaviorAction> actions)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(actions);

        foreach (var action in actions)
        {
            switch (action.Target)
            {
                case ProviderChatBehaviorActionTarget.RequestField:
                    body[action.Key] = ToJsonNode(action.Value);
                    break;
                case ProviderChatBehaviorActionTarget.NestedRequestField:
                    SetNestedField(body, action.Key, action.Value);
                    break;
                case ProviderChatBehaviorActionTarget.SystemMessagePrefix:
                    PrependToSystemMessage(messages, ToText(action.Value));
                    break;
            }
        }
    }

    private static void SetNestedField(JsonObject body, string dottedKey, object? value)
    {
        var segments = dottedKey.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        var current = body;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (current[segments[index]] is not JsonObject child)
            {
                child = new JsonObject();
                current[segments[index]] = child;
            }

            current = child;
        }

        current[segments[^1]] = ToJsonNode(value);
    }

    private static void PrependToSystemMessage(JsonArray? messages, string prefix)
    {
        if (messages == null || prefix.Length == 0)
        {
            return;
        }

        foreach (var message in messages)
        {
            if (message is not JsonObject messageObject)
            {
                continue;
            }

            var role = messageObject["role"]?.GetValue<string>();
            if (!string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (messageObject["content"] is JsonValue content && content.TryGetValue<string>(out var text))
            {
                messageObject["content"] = $"{prefix}\n\n{text}";
                return;
            }
        }

        messages.Insert(0, new JsonObject
        {
            ["role"] = "system",
            ["content"] = prefix
        });
    }

    private static string ToText(object? value) => value switch
    {
        null => string.Empty,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        _ => value.ToString() ?? string.Empty
    };

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        JsonNode node => node.DeepClone(),
        bool boolean => JsonValue.Create(boolean),
        string text => JsonValue.Create(text),
        int integer => JsonValue.Create(integer),
        long longValue => JsonValue.Create(longValue),
        double doubleValue => JsonValue.Create(doubleValue),
        decimal decimalValue => JsonValue.Create(decimalValue),
        _ => JsonValue.Create(value.ToString())
    };
}
