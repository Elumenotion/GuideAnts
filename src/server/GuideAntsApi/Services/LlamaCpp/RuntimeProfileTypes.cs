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
    ThinkingControl ThinkingControl);

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
