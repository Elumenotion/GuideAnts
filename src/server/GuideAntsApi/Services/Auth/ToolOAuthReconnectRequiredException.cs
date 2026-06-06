namespace GuideAntsApi.Services.Auth;

public sealed class ToolOAuthReconnectRequiredException : Exception
{
    public ToolOAuthReconnectRequiredException(IReadOnlyList<string> providerIds, string message)
        : base(message)
    {
        ProviderIds = providerIds;
    }

    public IReadOnlyList<string> ProviderIds { get; }
}
