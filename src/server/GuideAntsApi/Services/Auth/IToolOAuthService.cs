namespace GuideAntsApi.Services.Auth;

public sealed record ToolOAuthAuthorizeUrlRequest(
    string ClientId,
    string Tenant,
    IReadOnlyList<string> Scopes,
    string RedirectUri,
    string? ReturnUrl);

public sealed record ToolOAuthAuthorizeUrlResult(
    string AuthorizeUrl,
    string State,
    DateTime ExpiresAtUtc);

public sealed record ToolOAuthStatus(
    bool Connected,
    DateTime? ExpiresAt,
    IReadOnlyList<string> Scopes);

public interface IToolOAuthService
{
    Task<ToolOAuthAuthorizeUrlResult> CreateAuthorizeUrlAsync(
        Guid projectId,
        string providerId,
        Guid userId,
        ToolOAuthAuthorizeUrlRequest request,
        CancellationToken cancellationToken = default);

    Task<ToolOAuthStatus> CompleteCallbackAsync(
        Guid projectId,
        string providerId,
        Guid userId,
        string code,
        string state,
        CancellationToken cancellationToken = default);

    Task<ToolOAuthStatus> GetStatusAsync(
        Guid userId,
        string providerId,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(
        Guid userId,
        string providerId,
        CancellationToken cancellationToken = default);

    Task<Dictionary<string, string>> ResolveExternalAuthTokensForAssistantAsync(
        Guid userId,
        Guid projectId,
        Guid? assistantId,
        string assistantName,
        CancellationToken cancellationToken = default);
}
