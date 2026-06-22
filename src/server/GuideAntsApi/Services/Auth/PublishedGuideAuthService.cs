using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Auth;

public class PublishedGuideAuthService : IPublishedGuideAuthService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppJwtValidator _appJwtValidator;
    private readonly ILogger<PublishedGuideAuthService> _logger;

    /// <summary>
    /// Header name for API key authentication.
    /// </summary>
    public const string ApiKeyHeaderName = "x-guideants-apikey";

    public PublishedGuideAuthService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IAppJwtValidator appJwtValidator,
        ILogger<PublishedGuideAuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _appJwtValidator = appJwtValidator;
        _logger = logger;
    }

    public async Task<AuthValidationResult> ValidateAsync(
        Guid pubId,
        string? authorizationHeader,
        Guid projectId,
        Guid notebookId,
        CancellationToken ct = default,
        string? apiKeyHeader = null,
        string? appAuthCookieToken = null)
    {
        DataModel.Models.PublishedGuide? publishedGuide;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            publishedGuide = await db.PublishedGuides
                .AsNoTracking()
                .FirstOrDefaultAsync(pg => pg.Id == pubId && pg.Active, ct);
        }

        if (publishedGuide == null)
        {
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "invalid_published_guide",
                ErrorMessage = "Published guide not found or inactive"
            };
        }

        return publishedGuide.AuthMode switch
        {
            PublishedGuideAuthMode.AppIdentity => await ValidateAppIdentityAsync(
                authorizationHeader,
                appAuthCookieToken,
                ct),
            PublishedGuideAuthMode.ApiKey => ValidateApiKey(publishedGuide, apiKeyHeader),
            PublishedGuideAuthMode.Webhook => await ValidateWebhookAsync(
                publishedGuide,
                pubId,
                authorizationHeader,
                projectId,
                notebookId,
                ct),
            PublishedGuideAuthMode.Anonymous => new AuthValidationResult { IsValid = true },
            _ => new AuthValidationResult { IsValid = true }
        };
    }

    private async Task<AuthValidationResult> ValidateAppIdentityAsync(
        string? authorizationHeader,
        string? appAuthCookieToken,
        CancellationToken ct)
    {
        var token = ResolveAppIdentityJwt(appAuthCookieToken, authorizationHeader);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "authentication_required",
                ErrorMessage = "This published guide requires authentication"
            };
        }

        var validation = _appJwtValidator.Validate(token);
        if (!validation.IsValid)
        {
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = validation.ErrorCode ?? "invalid_token",
                ErrorMessage = validation.ErrorMessage ?? "Token validation failed"
            };
        }

        var principal = validation.Principal!;
        var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var securityStampValue = principal.FindFirstValue(JwtClaimTypes.SecurityStamp);

        if (!Guid.TryParse(userIdValue, out var userId) ||
            !Guid.TryParse(securityStampValue, out var tokenSecurityStamp))
        {
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "invalid_token",
                ErrorMessage = "Token claims are invalid"
            };
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var account = await db.UserRoles
            .AsNoTracking()
            .Where(userRole => userRole.UserId == userId)
            .Select(userRole => new { userRole.User.SecurityStamp, userRole.Role, userRole.User.ApprovedAt })
            .SingleOrDefaultAsync(ct);

        if (account is null || account.SecurityStamp != tokenSecurityStamp)
        {
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "invalid_token",
                ErrorMessage = "Token security stamp mismatch"
            };
        }

        if (account.Role == Role.Pending || account.ApprovedAt == null)
        {
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "user_not_approved",
                ErrorMessage = "User is not approved for this published guide"
            };
        }

        if (account.Role is not (Role.Reader or Role.Contributor or Role.Admin))
        {
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "user_not_approved",
                ErrorMessage = "User is not approved for this published guide"
            };
        }

        return new AuthValidationResult
        {
            IsValid = true,
            UserIdentity = userId.ToString(),
            InternalUserId = userId
        };
    }

    private static string? ResolveAppIdentityJwt(string? appAuthCookieToken, string? authorizationHeader)
    {
        if (!string.IsNullOrWhiteSpace(appAuthCookieToken))
        {
            return appAuthCookieToken;
        }

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        var trimmed = authorizationHeader.Trim();
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed["Bearer ".Length..].Trim();
        }

        return trimmed;
    }

    private async Task<AuthValidationResult> ValidateWebhookAsync(
        DataModel.Models.PublishedGuide publishedGuide,
        Guid pubId,
        string? authorizationHeader,
        Guid projectId,
        Guid notebookId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(publishedGuide.AuthValidationWebhookUrl))
        {
            return new AuthValidationResult { IsValid = true };
        }

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "authentication_required",
                ErrorMessage = "This published guide requires authentication"
            };
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var timeout = publishedGuide.AuthWebhookTimeoutSeconds ?? 5;
            httpClient.Timeout = TimeSpan.FromSeconds(timeout);

            var requestBody = new
            {
                token = authorizationHeader,
                publishedGuideId = pubId.ToString(),
                projectId = projectId.ToString(),
                notebookId = notebookId.ToString()
            };

            var response = await httpClient.PostAsJsonAsync(
                publishedGuide.AuthValidationWebhookUrl,
                requestBody,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                string? errorMessage = null;

                try
                {
                    var errorJson = JsonSerializer.Deserialize<JsonElement>(errorContent);
                    if (errorJson.TryGetProperty("error", out var errorProp))
                    {
                        errorMessage = errorProp.GetString();
                    }
                }
                catch
                {
                    // If can't parse JSON, use generic message
                }

                return new AuthValidationResult
                {
                    IsValid = false,
                    ErrorCode = "invalid_token",
                    ErrorMessage = errorMessage != null
                        ? $"Token validation failed: {errorMessage}"
                        : "Token validation failed"
                };
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var json = JsonSerializer.Deserialize<JsonElement>(content);

            if (!json.TryGetProperty("valid", out var validProp) || !validProp.GetBoolean())
            {
                var message = json.TryGetProperty("error", out var err)
                    ? err.GetString()
                    : "Token validation failed";

                return new AuthValidationResult
                {
                    IsValid = false,
                    ErrorCode = "invalid_token",
                    ErrorMessage = message ?? "Token validation failed"
                };
            }

            var userIdentity = json.TryGetProperty("userIdentity", out var userIdProp)
                ? userIdProp.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(userIdentity))
            {
                _logger.LogWarning(
                    "Webhook returned valid=true but no userIdentity for pubId {PubId}",
                    LogValueSanitizer.Sanitize(pubId));
            }

            return new AuthValidationResult
            {
                IsValid = true,
                UserIdentity = userIdentity
            };
        }
        catch (TaskCanceledException)
        {
            var timeoutSeconds = publishedGuide.AuthWebhookTimeoutSeconds ?? 5;
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "auth_service_unavailable",
                ErrorMessage = $"Authentication service did not respond within {timeoutSeconds} seconds"
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error calling auth webhook for published guide {PubId}", LogValueSanitizer.Sanitize(pubId));
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "auth_service_error",
                ErrorMessage = $"Authentication service returned an error: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling auth webhook for published guide {PubId}", LogValueSanitizer.Sanitize(pubId));
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "auth_service_error",
                ErrorMessage = $"Authentication service error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Validates an API key against the stored hash.
    /// </summary>
    private AuthValidationResult ValidateApiKey(DataModel.Models.PublishedGuide publishedGuide, string? apiKeyHeader)
    {
        if (string.IsNullOrWhiteSpace(apiKeyHeader))
        {
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "api_key_required",
                ErrorMessage = "This published guide requires an API key. Provide it via the x-guideants-apikey header."
            };
        }

        var providedHash = HashApiKey(apiKeyHeader);
        if (!string.Equals(providedHash, publishedGuide.ApiKeyHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Invalid API key attempt for published guide {PubId}", publishedGuide.Id);
            return new AuthValidationResult
            {
                IsValid = false,
                ErrorCode = "invalid_api_key",
                ErrorMessage = "The provided API key is invalid."
            };
        }

        return new AuthValidationResult
        {
            IsValid = true,
            UserIdentity = "api-key-user"
        };
    }

    /// <summary>
    /// Generates a new cryptographically secure API key.
    /// </summary>
    /// <returns>A 32-character alphanumeric API key with "gak_" prefix.</returns>
    public static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        var key = Convert.ToBase64String(bytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");
        return $"gak_{key}";
    }

    /// <summary>
    /// Computes SHA-256 hash of an API key for secure storage.
    /// </summary>
    public static string HashApiKey(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }

    public static string? ReadAppAuthCookie(HttpRequest request) =>
        request.Cookies.TryGetValue(AuthCookieConstants.CookieName, out var cookieToken) &&
        !string.IsNullOrWhiteSpace(cookieToken)
            ? cookieToken
            : null;

    public static int MapValidationFailureStatusCode(string? errorCode) =>
        errorCode switch
        {
            "authentication_required" or "api_key_required" or "invalid_token" or "invalid_api_key"
                => StatusCodes.Status401Unauthorized,
            "user_not_approved" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status503ServiceUnavailable
        };
}
