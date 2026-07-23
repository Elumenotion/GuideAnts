using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Services.LlamaCpp;

public sealed record SamplingParameterDefinition(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("max")] double Max,
    [property: JsonPropertyName("step")] double Step,
    [property: JsonPropertyName("default")] double Default,
    [property: JsonPropertyName("displayOrder")] int DisplayOrder,
    [property: JsonPropertyName("exposedInGuideBuilder")] bool ExposedInGuideBuilder);

[JsonConverter(typeof(ThinkingActionTargetConverter))]
public enum ThinkingActionTarget
{
    RequestField,
    NestedRequestField,
    SystemMessagePrefix
}

public sealed record ThinkingAction(
    [property: JsonPropertyName("target")] ThinkingActionTarget Target,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] object Value);

public sealed record ThinkingControl(
    [property: JsonPropertyName("defaultChoice")] string DefaultChoice,
    [property: JsonPropertyName("choiceActions")] IReadOnlyDictionary<string, IReadOnlyList<ThinkingAction>> ChoiceActions);

public sealed record RuntimeProfileData(
    string ProfileId,
    bool CombineSystemAndDeveloperMessages,
    string? ThoughtBlockPattern,
    IReadOnlyDictionary<string, SamplingParameterDefinition> SamplingParameters,
    ThinkingControl ThinkingControl,
    IReadOnlyDictionary<string, JsonElement> RequestFieldsWhenToolsPresent,
    string? DisplayName = null,
    string? Description = null);

public static class RuntimeProfileDataJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static RuntimeProfileData FromJsonStrings(
        string profileId,
        bool combineSystemAndDeveloperMessages,
        string? thoughtBlockPattern,
        string samplingParametersJson,
        string thinkingControlJson,
        string requestFieldsWhenToolsPresentJson,
        string? displayName = null,
        string? description = null)
    {
        return new RuntimeProfileData(
            profileId,
            combineSystemAndDeveloperMessages,
            thoughtBlockPattern,
            DeserializeSamplingParameters(samplingParametersJson, profileId),
            DeserializeThinkingControl(thinkingControlJson, profileId),
            DeserializeRequestFieldsWhenToolsPresent(requestFieldsWhenToolsPresentJson, profileId),
            displayName,
            description);
    }

    public static IReadOnlyDictionary<string, SamplingParameterDefinition> DeserializeSamplingParameters(
        string json,
        string profileId)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, SamplingParameterDefinition>>(json, JsonOptions);
            return dict ?? new Dictionary<string, SamplingParameterDefinition>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize SamplingParametersJson for profile '{profileId}'.", ex);
        }
    }

    public static ThinkingControl DeserializeThinkingControl(string json, string profileId)
    {
        try
        {
            var tc = JsonSerializer.Deserialize<ThinkingControl>(json, JsonOptions);
            return tc ?? new ThinkingControl("enabled", new Dictionary<string, IReadOnlyList<ThinkingAction>>());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize ThinkingControlJson for profile '{profileId}'.", ex);
        }
    }

    public static IReadOnlyDictionary<string, JsonElement> DeserializeRequestFieldsWhenToolsPresent(
        string json,
        string profileId)
    {
        try
        {
            return RuntimeProfileRequestFieldsValidator.ValidateAndNormalize(json);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize RequestFieldsWhenToolsPresentJson for profile '{profileId}'. {ex.Message}",
                ex);
        }
    }
}

internal sealed class ThinkingActionTargetConverter : JsonConverter<ThinkingActionTarget>
{
    public override ThinkingActionTarget Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "RequestField" => ThinkingActionTarget.RequestField,
            "NestedRequestField" => ThinkingActionTarget.NestedRequestField,
            "SystemMessagePrefix" => ThinkingActionTarget.SystemMessagePrefix,
            _ => throw new JsonException($"Unknown ThinkingActionTarget '{value}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, ThinkingActionTarget value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
