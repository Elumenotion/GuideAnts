namespace GuideAntsApi.Services.Auth;

public interface IPublishedGuideAuthService
{
    /// <summary>
    /// Validates authentication for a published guide conversation.
    /// Supports both webhook-based auth (X-Published-Auth header) and API key auth (x-guideants-apikey header).
    /// </summary>
    /// <param name="pubId">Published guide ID</param>
    /// <param name="authorizationHeader">Authorization header value for webhook auth (e.g., "Bearer token")</param>
    /// <param name="projectId">Project ID for context</param>
    /// <param name="notebookId">Notebook ID for context</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="apiKeyHeader">API key header value (x-guideants-apikey)</param>
    /// <param name="appAuthCookieToken">GuideAnts.Auth HttpOnly cookie value for AppIdentity mode</param>
    /// <returns>Validation result with user identity or error details</returns>
    Task<AuthValidationResult> ValidateAsync(
        Guid pubId,
        string? authorizationHeader,
        Guid projectId,
        Guid notebookId,
        CancellationToken ct = default,
        string? apiKeyHeader = null,
        string? appAuthCookieToken = null);
}

public class AuthValidationResult
{
    public bool IsValid { get; set; }
    public string? UserIdentity { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Internal <c>Users.Id</c> resolved for AppIdentity-mode published guides.
    /// Declared for Phase 2 use; no logic reads or writes it in Phase 1.
    /// </summary>
    public Guid? InternalUserId { get; set; }
}

