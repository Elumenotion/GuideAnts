using System.Text.Json;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Resolves effective chat models from entity configuration and persisted <c>ChatDefaults</c>
/// (via <see cref="IChatDefaultsStore"/>, same backing as the Settings API).
/// </summary>
public sealed class ChatModelResolver : IChatModelResolver
{
    private readonly IChatDefaultsStore _chatDefaultsStore;
    private readonly IChatTargetResolver _chatTargetResolver;

    public ChatModelResolver(IChatDefaultsStore chatDefaultsStore, IChatTargetResolver chatTargetResolver)
    {
        _chatDefaultsStore = chatDefaultsStore ?? throw new ArgumentNullException(nameof(chatDefaultsStore));
        _chatTargetResolver = chatTargetResolver ?? throw new ArgumentNullException(nameof(chatTargetResolver));
    }

    public ResolvedChatModel Resolve(string? entityModelId)
    {
        var chatDefaults = _chatDefaultsStore.Current;
        var overrideAll = chatDefaults.OverrideAllChatModels;
        var defaultId = (chatDefaults.DefaultModelId ?? string.Empty).Trim();

        if (overrideAll)
        {
            if (string.IsNullOrWhiteSpace(defaultId))
            {
                throw RoutingException.ModelNotReady(
                    "(default)",
                    "Override all chat models is enabled but no default catalog model is configured.",
                    serviceId: "Chat",
                    action: "Open Settings \u2192 Overview (or Connections) and set Default Chat Model \u2192 Default model id.");
            }

            return BuildResolvedModel(
                defaultId,
                ChatModelReferenceKind.OverriddenToDefault,
                ParameterAuthority.GlobalOverride,
                ChatDefaultsExecutionParameters.FromSnapshot(chatDefaults, defaultId));
        }

        var trimmedEntity = (entityModelId ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmedEntity))
        {
            return BuildResolvedModel(
                trimmedEntity,
                ChatModelReferenceKind.Direct,
                ParameterAuthority.AssistantDefinition,
                EmptyParameters);
        }

        if (!string.IsNullOrWhiteSpace(defaultId))
        {
            return BuildResolvedModel(
                defaultId,
                ChatModelReferenceKind.DefaultedTo,
                ParameterAuthority.AssistantDefinition,
                ChatDefaultsExecutionParameters.FromSnapshot(chatDefaults, defaultId));
        }

        throw RoutingException.ModelNotReady(
            "(unset)",
            "No chat model is configured on this assistant and no global default is set.",
            serviceId: "Chat",
            action: "Pick a model in Guide Builder, or configure Settings \u2192 Overview \u2192 Default Chat Model.");
    }

    private ResolvedChatModel BuildResolvedModel(
        string modelId,
        ChatModelReferenceKind referenceKind,
        ParameterAuthority authority,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var target = _chatTargetResolver.Resolve(modelId);
        var policy = new ResolvedExecutionPolicy(modelId, target.Provider, authority, parameters);
        return new ResolvedChatModel(modelId, referenceKind, policy);
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyParameters =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
