using System.Text.Json.Nodes;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Endpoints.Settings;

internal static class SettingsChatDefaultsMapper
{
    public static ChatDefaultsDto MapChatDefaults(SettingsSectionDto section)
    {
        var p = section.Payload;
        return new ChatDefaultsDto(
            GetPayloadString(p, "DefaultModelId"),
            GetPayloadBool(p, "OverrideAllChatModels", false),
            GetPayloadDouble(p, "Temperature"),
            GetPayloadDouble(p, "TopP"),
            GetPayloadString(p, "ReasoningEffort"),
            GetPayloadString(p, "SamplingParametersJson"),
            section.RowVersion);
    }

    public static JsonObject BuildChatDefaultsPayload(UpdateChatDefaultsRequest request)
    {
        return new JsonObject
        {
            ["DefaultModelId"] = JsonValue.Create(request.DefaultModelId),
            ["OverrideAllChatModels"] = JsonValue.Create(request.OverrideAllChatModels),
            ["Temperature"] = JsonValue.Create(request.Temperature),
            ["TopP"] = JsonValue.Create(request.TopP),
            ["ReasoningEffort"] = JsonValue.Create(request.ReasoningEffort),
            ["SamplingParametersJson"] = JsonValue.Create(request.SamplingParametersJson)
        };
    }

    private static string? GetPayloadString(JsonObject payload, string name)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node switch
        {
            JsonValue jv when jv.TryGetValue<string>(out var s) => s,
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
