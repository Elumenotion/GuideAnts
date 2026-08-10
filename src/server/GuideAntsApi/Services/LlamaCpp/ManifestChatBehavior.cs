using System.Text.Json;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.LlamaCpp;

public static class ManifestChatBehavior
{
    public static string SerializeSamplingParameters(LlamaCatalogChatBehaviorDto chatBehavior) =>
        chatBehavior.SamplingParametersJson.GetRawText();

    public static string SerializeThinkingControl(LlamaCatalogChatBehaviorDto chatBehavior) =>
        chatBehavior.ThinkingControlJson.GetRawText();

    public static string SerializeRequestFieldsWhenToolsPresent(LlamaCatalogChatBehaviorDto chatBehavior)
    {
        if (chatBehavior.RequestFieldsWhenToolsPresent is null
            || chatBehavior.RequestFieldsWhenToolsPresent.Value.ValueKind == JsonValueKind.Null
            || chatBehavior.RequestFieldsWhenToolsPresent.Value.ValueKind == JsonValueKind.Undefined)
        {
            return "{}";
        }

        return chatBehavior.RequestFieldsWhenToolsPresent.Value.GetRawText();
    }

    public static void Validate(LlamaCatalogChatBehaviorDto chatBehavior, string context)
    {
        var samplingJson = SerializeSamplingParameters(chatBehavior);
        var thinkingJson = SerializeThinkingControl(chatBehavior);
        var requestFieldsJson = SerializeRequestFieldsWhenToolsPresent(chatBehavior);

        RuntimeProfileDataJson.FromJsonStrings(
            context,
            chatBehavior.CombineSystemAndDeveloperMessages,
            chatBehavior.ThoughtBlockPattern,
            samplingJson,
            thinkingJson,
            requestFieldsJson,
            displayName: null,
            description: null);
    }

    public static string? DeriveReasoningChoicesJson(LlamaCatalogChatBehaviorDto chatBehavior) =>
        ModelChatBehavior.DeriveReasoningChoicesJson(SerializeThinkingControl(chatBehavior));
}
