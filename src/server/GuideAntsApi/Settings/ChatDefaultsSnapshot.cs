using System.Text.Json.Nodes;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Settings;

/// <summary>
/// Runtime snapshot of persisted <c>ChatDefaults</c> (same source as Settings API / DB).
/// </summary>
public sealed record ChatDefaultsSnapshot(
    string? DefaultModelId,
    bool OverrideAllChatModels,
    double? Temperature,
    double? TopP,
    string? ReasoningEffort,
    string? SamplingParametersJson)
{
    public static ChatDefaultsSnapshot Empty { get; } = new(
        DefaultModelId: null,
        OverrideAllChatModels: false,
        Temperature: null,
        TopP: null,
        ReasoningEffort: null,
        SamplingParametersJson: null);

    public static ChatDefaultsSnapshot FromSection(SettingsSectionDto? section)
    {
        if (section?.Payload is null)
        {
            return Empty;
        }

        return FromPayload(section.Payload);
    }

    public static ChatDefaultsSnapshot FromPayload(JsonObject payload)
    {
        return new ChatDefaultsSnapshot(
            GetPayloadString(payload, "DefaultModelId"),
            GetPayloadBool(payload, "OverrideAllChatModels", defaultValue: false),
            GetPayloadDouble(payload, "Temperature"),
            GetPayloadDouble(payload, "TopP"),
            GetPayloadString(payload, "ReasoningEffort"),
            GetPayloadString(payload, "SamplingParametersJson"));
    }

    private static string? GetPayloadString(JsonObject payload, string name)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node switch
        {
            JsonValue jv when jv.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            _ => null
        };
    }

    private static bool GetPayloadBool(JsonObject payload, string name, bool defaultValue)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is not JsonValue jv)
        {
            return defaultValue;
        }

        return jv.TryGetValue<bool>(out var b) ? b : defaultValue;
    }

    private static double? GetPayloadDouble(JsonObject payload, string name)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is not JsonValue jv)
        {
            return null;
        }

        if (jv.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (jv.TryGetValue<long>(out var l))
        {
            return l;
        }

        return null;
    }
}
