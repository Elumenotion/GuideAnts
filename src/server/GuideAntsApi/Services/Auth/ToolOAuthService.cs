using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Auth;

public sealed class ToolOAuthService : IToolOAuthService
{
    private const int StateLifetimeMinutes = 10;
    private const int RefreshLeadMinutes = 5;

    private static readonly SettingsSectionDefinition TokenCipherDefinition = new()
    {
        SectionName = "ToolOAuthTokens",
        Properties = new[]
        {
            new SettingsPropertyDefinition("value", "ToolOAuthTokens:value", IsSecret: true)
        }
    };

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<SettingsSecretsOptions> _settingsSecretsOptions;
    private readonly ILogger<ToolOAuthService> _logger;

    public ToolOAuthService(
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<SettingsSecretsOptions> settingsSecretsOptions,
        ILogger<ToolOAuthService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settingsSecretsOptions = settingsSecretsOptions ?? throw new ArgumentNullException(nameof(settingsSecretsOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ToolOAuthAuthorizeUrlResult> CreateAuthorizeUrlAsync(
        Guid projectId,
        string providerId,
        Guid userId,
        ToolOAuthAuthorizeUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("ProviderId is required.", nameof(providerId));
        }

        if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirectUri)
            || (redirectUri.Scheme != Uri.UriSchemeHttp && redirectUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("RedirectUri must be an absolute http/https URL.", nameof(request));
        }

        var scopes = NormalizeScopes(request.Scopes);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("At least one OAuth scope is required.", nameof(request));
        }

        var projectOverride = await _db.ProjectExternalAuths
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ProjectId == projectId
                        && item.ProviderId == providerId
                        && item.AuthType == "oauth",
                cancellationToken);

        var clientId = !string.IsNullOrWhiteSpace(projectOverride?.ClientId)
            ? projectOverride!.ClientId!.Trim()
            : request.ClientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("OAuth clientId is not configured for this provider.");
        }

        var tenant = !string.IsNullOrWhiteSpace(projectOverride?.Tenant)
            ? projectOverride!.Tenant!.Trim()
            : (string.IsNullOrWhiteSpace(request.Tenant) ? "organizations" : request.Tenant.Trim());

        await CleanupExpiredAuthorizationStatesAsync(cancellationToken);

        var state = CreateBase64Url(32);
        var codeVerifier = CreateBase64Url(64);
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(StateLifetimeMinutes);

        var stateRow = new OAuthAuthorizationState
        {
            State = state,
            UserId = userId,
            ProviderId = providerId,
            CodeVerifier = codeVerifier,
            ClientId = clientId,
            Tenant = tenant,
            Scopes = string.Join(' ', scopes),
            RedirectUri = request.RedirectUri,
            ReturnUrl = NormalizeReturnUrl(request.ReturnUrl),
            Created = now,
            ExpiresAt = expiresAt
        };

        _db.OAuthAuthorizationStates.Add(stateRow);
        await _db.SaveChangesAsync(cancellationToken);

        var authorizeUrl =
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(request.RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(stateRow.Scopes!)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            "&code_challenge_method=S256";

        return new ToolOAuthAuthorizeUrlResult(authorizeUrl, state, expiresAt);
    }

    public async Task<ToolOAuthStatus> CompleteCallbackAsync(
        Guid projectId,
        string providerId,
        Guid userId,
        string code,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Authorization code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("State is required.", nameof(state));
        }

        await CleanupExpiredAuthorizationStatesAsync(cancellationToken);

        var stateRow = await _db.OAuthAuthorizationStates
            .FirstOrDefaultAsync(item => item.State == state, cancellationToken);

        if (stateRow == null)
        {
            throw new InvalidOperationException("OAuth state is invalid or expired.");
        }

        if (stateRow.UserId != userId || !string.Equals(stateRow.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("OAuth state does not match the authenticated user/provider.");
        }

        if (stateRow.ExpiresAt <= DateTime.UtcNow)
        {
            _db.OAuthAuthorizationStates.Remove(stateRow);
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("OAuth state has expired.");
        }

        // Consume state once to prevent replay attempts.
        _db.OAuthAuthorizationStates.Remove(stateRow);
        await _db.SaveChangesAsync(cancellationToken);

        var exchange = await ExchangeAuthorizationCodeAsync(
            tenant: stateRow.Tenant,
            clientId: stateRow.ClientId,
            code: code,
            codeVerifier: stateRow.CodeVerifier,
            redirectUri: stateRow.RedirectUri,
            scopes: stateRow.Scopes,
            cancellationToken);

        var now = DateTime.UtcNow;
        var tokenRow = await _db.ExternalOAuthTokens
            .FirstOrDefaultAsync(
                item => item.UserId == userId && item.ProviderId == providerId,
                cancellationToken);

        if (tokenRow == null)
        {
            tokenRow = new ExternalOAuthToken
            {
                UserId = userId,
                ProviderId = providerId,
                Created = now
            };
            _db.ExternalOAuthTokens.Add(tokenRow);
        }

        tokenRow.ProjectId = projectId;
        tokenRow.AccessTokenEncrypted = EncryptToken(exchange.AccessToken);
        tokenRow.RefreshTokenEncrypted = string.IsNullOrWhiteSpace(exchange.RefreshToken)
            ? null
            : EncryptToken(exchange.RefreshToken);
        tokenRow.ExpiresAt = now.AddSeconds(exchange.ExpiresInSeconds);
        tokenRow.Scope = string.IsNullOrWhiteSpace(exchange.Scope) ? stateRow.Scopes : exchange.Scope;
        tokenRow.Updated = now;

        await _db.SaveChangesAsync(cancellationToken);

        return new ToolOAuthStatus(
            Connected: true,
            ExpiresAt: tokenRow.ExpiresAt,
            Scopes: ParseScopes(tokenRow.Scope));
    }

    public async Task<ToolOAuthStatus> GetStatusAsync(
        Guid userId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var tokenRow = await _db.ExternalOAuthTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.UserId == userId && item.ProviderId == providerId,
                cancellationToken);

        if (tokenRow == null)
        {
            return new ToolOAuthStatus(false, null, Array.Empty<string>());
        }

        return new ToolOAuthStatus(
            Connected: true,
            ExpiresAt: tokenRow.ExpiresAt,
            Scopes: ParseScopes(tokenRow.Scope));
    }

    public async Task DisconnectAsync(
        Guid userId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var tokenRows = await _db.ExternalOAuthTokens
            .Where(item => item.UserId == userId && item.ProviderId == providerId)
            .ToListAsync(cancellationToken);

        if (tokenRows.Count == 0)
        {
            return;
        }

        _db.ExternalOAuthTokens.RemoveRange(tokenRows);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Dictionary<string, string>> ResolveExternalAuthTokensForAssistantAsync(
        Guid userId,
        Guid projectId,
        Guid? assistantId,
        string assistantName,
        CancellationToken cancellationToken = default)
    {
        var providerConfigs = await GetAssistantOAuthProviderConfigsAsync(
            projectId,
            assistantId,
            assistantName,
            cancellationToken);

        if (providerConfigs.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var providerIds = providerConfigs.Select(config => config.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tokenRows = await _db.ExternalOAuthTokens
            .Where(item => item.UserId == userId && providerIds.Contains(item.ProviderId))
            .ToListAsync(cancellationToken);
        var tokenByProvider = tokenRows.ToDictionary(item => item.ProviderId, StringComparer.OrdinalIgnoreCase);

        var reconnectProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var provider in providerConfigs)
        {
            if (!tokenByProvider.TryGetValue(provider.ProviderId, out var tokenRow))
            {
                reconnectProviders.Add(provider.ProviderId);
                continue;
            }

            var accessToken = DecryptToken(tokenRow.AccessTokenEncrypted);
            var refreshToken = DecryptToken(tokenRow.RefreshTokenEncrypted);

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _db.ExternalOAuthTokens.Remove(tokenRow);
                reconnectProviders.Add(provider.ProviderId);
                continue;
            }

            if (tokenRow.ExpiresAt <= now.AddMinutes(RefreshLeadMinutes))
            {
                if (string.IsNullOrWhiteSpace(refreshToken)
                    || string.IsNullOrWhiteSpace(provider.ClientId)
                    || string.IsNullOrWhiteSpace(provider.Tenant))
                {
                    _db.ExternalOAuthTokens.Remove(tokenRow);
                    reconnectProviders.Add(provider.ProviderId);
                    continue;
                }

                try
                {
                    var refreshed = await RefreshAccessTokenAsync(
                        tenant: provider.Tenant,
                        clientId: provider.ClientId,
                        refreshToken: refreshToken,
                        scopes: tokenRow.Scope,
                        cancellationToken);

                    accessToken = refreshed.AccessToken;
                    tokenRow.AccessTokenEncrypted = EncryptToken(refreshed.AccessToken);
                    if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
                    {
                        tokenRow.RefreshTokenEncrypted = EncryptToken(refreshed.RefreshToken);
                    }

                    tokenRow.ExpiresAt = DateTime.UtcNow.AddSeconds(refreshed.ExpiresInSeconds);
                    if (!string.IsNullOrWhiteSpace(refreshed.Scope))
                    {
                        tokenRow.Scope = refreshed.Scope;
                    }

                    tokenRow.Updated = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "OAuth refresh failed for user {UserId} provider {ProviderId}; requiring reconnect.",
                        LogValueSanitizer.Sanitize(userId),
                        LogValueSanitizer.Sanitize(provider.ProviderId));
                    _db.ExternalOAuthTokens.Remove(tokenRow);
                    reconnectProviders.Add(provider.ProviderId);
                    continue;
                }
            }

            var valueTemplate = string.IsNullOrWhiteSpace(provider.ValueTemplate)
                ? "Bearer {access_token}"
                : provider.ValueTemplate;

            var resolvedValue = valueTemplate.Replace(
                "{access_token}",
                accessToken,
                StringComparison.OrdinalIgnoreCase);

            foreach (var apiHost in provider.ApiHosts)
            {
                resolvedTokens[apiHost] = resolvedValue;
            }

            // Include provider ID to preserve compatibility for callers keyed by provider.
            resolvedTokens[provider.ProviderId] = resolvedValue;
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (reconnectProviders.Count > 0)
        {
            throw new ToolOAuthReconnectRequiredException(
                reconnectProviders.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
                "OAuth reconnect required for one or more providers.");
        }

        return resolvedTokens;
    }

    private async Task<List<AssistantOAuthProviderConfig>> GetAssistantOAuthProviderConfigsAsync(
        Guid projectId,
        Guid? assistantId,
        string assistantName,
        CancellationToken cancellationToken)
    {
        var resolvedAssistantId = assistantId;
        if (!resolvedAssistantId.HasValue)
        {
            resolvedAssistantId = await _db.Assistants
                .AsNoTracking()
                .Where(item => item.Name == assistantName && item.IsActive)
                .OrderBy(item => item.IsGlobal)
                .Select(item => (Guid?)item.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (!resolvedAssistantId.HasValue)
        {
            return [];
        }

        var providerRows = await _db.AssistantOpenApiSchemas
            .AsNoTracking()
            .Where(schema => schema.AssistantId == resolvedAssistantId.Value
                             && schema.AuthProvider != null
                             && (schema.AuthProvider.AuthType == "oauth" || schema.AuthProvider.AuthType == "azure_oauth"))
            .Select(schema => new
            {
                ProviderId = schema.AuthProvider!.ProviderId,
                ApiHost = schema.ApiHost,
                ClientId = schema.AuthProvider.ClientId,
                Tenant = schema.AuthProvider.Tenant,
                ValueTemplate = schema.AuthProvider.ValueTemplate,
                Scopes = schema.AuthProvider.Scopes.Select(scope => scope.Scope).ToList()
            })
            .ToListAsync(cancellationToken);

        if (providerRows.Count == 0)
        {
            return [];
        }

        var merged = new Dictionary<string, AssistantOAuthProviderConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in providerRows)
        {
            if (!merged.TryGetValue(row.ProviderId, out var config))
            {
                config = new AssistantOAuthProviderConfig
                {
                    ProviderId = row.ProviderId,
                    ClientId = row.ClientId,
                    Tenant = row.Tenant,
                    ValueTemplate = row.ValueTemplate
                };
                merged[row.ProviderId] = config;
            }

            if (string.IsNullOrWhiteSpace(config.ClientId) && !string.IsNullOrWhiteSpace(row.ClientId))
            {
                config.ClientId = row.ClientId;
            }

            if (string.IsNullOrWhiteSpace(config.Tenant) && !string.IsNullOrWhiteSpace(row.Tenant))
            {
                config.Tenant = row.Tenant;
            }

            if (string.IsNullOrWhiteSpace(config.ValueTemplate) && !string.IsNullOrWhiteSpace(row.ValueTemplate))
            {
                config.ValueTemplate = row.ValueTemplate;
            }

            foreach (var scope in row.Scopes.Where(scope => !string.IsNullOrWhiteSpace(scope)))
            {
                config.Scopes.Add(scope);
            }

            if (!string.IsNullOrWhiteSpace(row.ApiHost))
            {
                config.ApiHosts.Add(row.ApiHost);
            }
        }

        var projectOverrides = await _db.ProjectExternalAuths
            .AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.AuthType == "oauth")
            .ToListAsync(cancellationToken);
        var overrideByProvider = projectOverrides.ToDictionary(item => item.ProviderId, StringComparer.OrdinalIgnoreCase);

        foreach (var config in merged.Values)
        {
            if (overrideByProvider.TryGetValue(config.ProviderId, out var item))
            {
                if (!string.IsNullOrWhiteSpace(item.ClientId))
                {
                    config.ClientId = item.ClientId.Trim();
                }

                if (!string.IsNullOrWhiteSpace(item.Tenant))
                {
                    config.Tenant = item.Tenant.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(config.Tenant))
            {
                config.Tenant = "organizations";
            }
        }

        return merged.Values.ToList();
    }

    private async Task<TokenExchangeResult> ExchangeAuthorizationCodeAsync(
        string tenant,
        string clientId,
        string code,
        string codeVerifier,
        string redirectUri,
        string? scopes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token");

        var fields = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("code_verifier", codeVerifier)
        };
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            fields.Add(new("scope", scopes));
        }

        request.Content = new FormUrlEncodedContent(fields);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OAuth token exchange failed with HTTP {(int)response.StatusCode}.");
        }

        return ParseTokenExchangePayload(payload);
    }

    private async Task<TokenExchangeResult> RefreshAccessTokenAsync(
        string tenant,
        string clientId,
        string refreshToken,
        string? scopes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token");

        var fields = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken)
        };
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            fields.Add(new("scope", scopes));
        }

        request.Content = new FormUrlEncodedContent(fields);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OAuth token refresh failed with HTTP {(int)response.StatusCode}.");
        }

        return ParseTokenExchangePayload(payload);
    }

    private static TokenExchangeResult ParseTokenExchangePayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var accessToken = root.TryGetProperty("access_token", out var accessTokenProperty)
            ? accessTokenProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("OAuth provider did not return an access_token.");
        }

        var refreshToken = root.TryGetProperty("refresh_token", out var refreshTokenProperty)
            ? refreshTokenProperty.GetString()
            : null;
        var scope = root.TryGetProperty("scope", out var scopeProperty)
            ? scopeProperty.GetString()
            : null;

        var expiresIn = 3600;
        if (root.TryGetProperty("expires_in", out var expiresInProperty))
        {
            if (expiresInProperty.ValueKind == JsonValueKind.Number && expiresInProperty.TryGetInt32(out var parsedNumber))
            {
                expiresIn = parsedNumber;
            }
            else if (expiresInProperty.ValueKind == JsonValueKind.String
                     && int.TryParse(expiresInProperty.GetString(), out var parsedString))
            {
                expiresIn = parsedString;
            }
        }

        return new TokenExchangeResult(accessToken, refreshToken, scope, expiresIn);
    }

    private async Task CleanupExpiredAuthorizationStatesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expiredRows = await _db.OAuthAuthorizationStates
            .Where(item => item.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        if (expiredRows.Count == 0)
        {
            return;
        }

        _db.OAuthAuthorizationStates.RemoveRange(expiredRows);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private string EncryptToken(string plaintext)
    {
        var payload = new JsonObject
        {
            ["value"] = JsonValue.Create(plaintext)
        };

        var encryptedPayload = ApplicationSettingsJson.EncryptSecrets(
            TokenCipherDefinition,
            payload,
            _settingsSecretsOptions.CurrentValue);

        return ApplicationSettingsJson.NodeToString(encryptedPayload["value"]);
    }

    private string? DecryptToken(string? encryptedValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
        {
            return null;
        }

        var payload = new JsonObject
        {
            ["value"] = JsonValue.Create(encryptedValue)
        };

        var decryptedPayload = ApplicationSettingsJson.DecryptSecrets(
            TokenCipherDefinition,
            payload,
            _settingsSecretsOptions.CurrentValue);

        var plaintext = ApplicationSettingsJson.NodeToString(decryptedPayload["value"]);
        return string.IsNullOrWhiteSpace(plaintext) ? null : plaintext;
    }

    private static List<string> NormalizeScopes(IEnumerable<string>? scopes)
    {
        if (scopes == null)
        {
            return [];
        }

        return scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ParseScopes(string? rawScopes)
    {
        if (string.IsNullOrWhiteSpace(rawScopes))
        {
            return Array.Empty<string>();
        }

        return rawScopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return null;
        }

        var trimmed = returnUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return null;
        }

        return trimmed.StartsWith("/", StringComparison.Ordinal) ? trimmed : $"/{trimmed}";
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return ToBase64Url(bytes);
    }

    private static string CreateBase64Url(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return ToBase64Url(bytes);
    }

    private static string ToBase64Url(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class AssistantOAuthProviderConfig
    {
        public required string ProviderId { get; init; }
        public HashSet<string> ApiHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ClientId { get; set; }
        public string? Tenant { get; set; }
        public string? ValueTemplate { get; set; }
        public HashSet<string> Scopes { get; } = new(StringComparer.Ordinal);
    }

    private sealed record TokenExchangeResult(
        string AccessToken,
        string? RefreshToken,
        string? Scope,
        int ExpiresInSeconds);
}
