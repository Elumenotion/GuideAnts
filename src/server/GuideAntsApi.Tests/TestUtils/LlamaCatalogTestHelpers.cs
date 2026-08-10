using System.Text.Json;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Tests.TestUtils;

public static class LlamaCatalogTestHelpers
{
    public const string MinimalThinkingControlJson = """{"defaultChoice":"medium","choiceActions":{"medium":[]}}""";
    public const string MinimalSamplingParametersJson = "{}";
    public const string MinimalRequestFieldsWhenToolsPresentJson = """{"parallel_tool_calls":true}""";

    public static LlamaCatalogChatBehaviorDto CreateChatBehaviorDto(
        bool combineSystemAndDeveloperMessages = true,
        string? thoughtBlockPattern = null,
        string samplingParametersJson = MinimalSamplingParametersJson,
        string thinkingControlJson = MinimalThinkingControlJson,
        string? requestFieldsWhenToolsPresentJson = MinimalRequestFieldsWhenToolsPresentJson)
    {
        using var samplingDoc = JsonDocument.Parse(samplingParametersJson);
        using var thinkingDoc = JsonDocument.Parse(thinkingControlJson);
        JsonElement? requestFields = null;
        if (requestFieldsWhenToolsPresentJson is not null)
        {
            using var requestFieldsDoc = JsonDocument.Parse(requestFieldsWhenToolsPresentJson);
            requestFields = requestFieldsDoc.RootElement.Clone();
        }

        return new LlamaCatalogChatBehaviorDto(
            combineSystemAndDeveloperMessages,
            thoughtBlockPattern,
            samplingDoc.RootElement.Clone(),
            thinkingDoc.RootElement.Clone(),
            requestFields);
    }

    public static LlamaCatalogDefaultsDto CreateDefaults(
        string catalogModelId,
        string routerModelId,
        string targetDirectory,
        LlamaCatalogMmprojDto? mmproj = null,
        IReadOnlyDictionary<string, string>? routerPreset = null,
        LlamaCatalogChatBehaviorDto? chatBehavior = null) =>
        new(
            catalogModelId,
            routerModelId,
            targetDirectory,
            mmproj,
            routerPreset ?? new Dictionary<string, string> { ["ctx-size"] = "8192" },
            chatBehavior ?? CreateChatBehaviorDto());

    public static (string Sampling, string? Reasoning, string Thinking, string RequestFields, bool Combine, string? Thought)
        RowOwnedChatBehaviorFields() =>
        (
            MinimalSamplingParametersJson,
            """["medium"]""",
            MinimalThinkingControlJson,
            MinimalRequestFieldsWhenToolsPresentJson,
            true,
            null);
}
