namespace AntRunner.Chat.LlamaCpp;

public enum ThinkingActionTarget
{
    RequestField,
    NestedRequestField,
    SystemMessagePrefix
}

public sealed record ThinkingAction(
    ThinkingActionTarget Target,
    string Key,
    object Value);

public sealed record ThinkingControl(
    string DefaultChoice,
    IReadOnlyDictionary<string, IReadOnlyList<ThinkingAction>> ChoiceActions);

public sealed record LlamaCppRuntimeProfileData(
    string ProfileId,
    bool CombineSystemAndDeveloperMessages,
    string? ThoughtBlockPattern,
    IReadOnlyDictionary<string, double> SamplingDefaults,
    ThinkingControl ThinkingControl);
