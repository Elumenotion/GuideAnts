using AntRunner.Chat.Abstractions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services.ConversationLockGate;

public sealed class ConversationLockGateEligibility : IConversationLockGateEligibility
{
    internal const string LocalEmbeddingsProviderSection = "LocalServiceHosts:EmbeddingsBaseUrl";
    internal const string LocalChatProvider = "llama-cpp";

    private readonly IChatModelResolver _chatModelResolver;
    private readonly IServiceModeResolver _serviceModeResolver;

    public ConversationLockGateEligibility(
        IChatModelResolver chatModelResolver,
        IServiceModeResolver serviceModeResolver)
    {
        _chatModelResolver = chatModelResolver ?? throw new ArgumentNullException(nameof(chatModelResolver));
        _serviceModeResolver = serviceModeResolver ?? throw new ArgumentNullException(nameof(serviceModeResolver));
    }

    public async Task<bool> BothUseLocalAiAsync(CancellationToken cancellationToken = default)
    {
        if (!ChatUsesLocalAi())
        {
            return false;
        }

        return await EmbeddingsUseLocalAiAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool ChatUsesLocalAi()
    {
        try
        {
            var resolved = _chatModelResolver.Resolve(entityModelId: null);
            return string.Equals(
                resolved.ExecutionPolicy.Provider,
                LocalChatProvider,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (RoutingException)
        {
            return false;
        }
    }

    private async Task<bool> EmbeddingsUseLocalAiAsync(CancellationToken cancellationToken)
    {
        try
        {
            var mode = await _serviceModeResolver
                .ResolveAsync(RoutedServiceNames.Embeddings, modeId: null, cancellationToken)
                .ConfigureAwait(false);

            return string.Equals(
                mode.ProviderSection,
                LocalEmbeddingsProviderSection,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (RoutingException)
        {
            return false;
        }
    }
}
