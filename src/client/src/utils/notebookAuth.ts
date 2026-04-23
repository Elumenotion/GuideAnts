import { NotebookAuthProviderDto, NotebookTemplateDto } from '../types/project';

/**
 * Refreshes OAuth tokens for a specific provider using the stored refresh token.
 */
export async function refreshOAuthTokens(
    projectId: string, 
    providerId: string,
    provider: NotebookAuthProviderDto
): Promise<boolean> {
    const tokenKey = `oauth_tokens_${projectId}_${providerId}`;
    const tokenData = localStorage.getItem(tokenKey);
    
    if (!tokenData) {
        return false; // No token data to refresh
    }

    let parsedTokens;
    try {
        parsedTokens = JSON.parse(tokenData);
    } catch {
        return false; // Invalid token data
    }

    if (!parsedTokens.refreshToken) {
        return false; // No refresh token available
    }

    try {
        // Use Microsoft OAuth2 refresh token endpoint
        const refreshResponse = await fetch(`https://login.microsoftonline.com/organizations/oauth2/v2.0/token`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams({
                client_id: provider.clientId || '',
                grant_type: 'refresh_token',
                refresh_token: parsedTokens.refreshToken,
                scope: parsedTokens.scope || ''
            })
        });

        if (!refreshResponse.ok) {
            // Refresh failed - remove invalid tokens
            localStorage.removeItem(tokenKey);
            return false;
        }

        const newTokens = await refreshResponse.json();
        
        // Store the new tokens
        const updatedTokenData = {
            accessToken: newTokens.access_token,
            refreshToken: newTokens.refresh_token || parsedTokens.refreshToken, // Keep old refresh token if new one not provided
            expiresAt: Date.now() + (newTokens.expires_in * 1000),
            scope: newTokens.scope || parsedTokens.scope,
            providerId: parsedTokens.providerId,
            projectId: parsedTokens.projectId,
            created: parsedTokens.created,
            refreshed: Date.now()
        };

        localStorage.setItem(tokenKey, JSON.stringify(updatedTokenData));
        
        console.log(`OAuth tokens refreshed for ${providerId} in project ${projectId}`);
        return true;

    } catch (error) {
        console.error('Error refreshing OAuth tokens:', error);
        // Remove invalid tokens on error
        localStorage.removeItem(tokenKey);
        return false;
    }
}

/**
 * Checks if a notebook template has OAuth providers that require authentication
 * and if the user has valid tokens for all required providers.
 * Treats tokens as needing refresh if they expire within 5 minutes.
 */
export function checkNotebookAuthRequirements(
    template: NotebookTemplateDto | null,
    projectId: string
): {
    needsAuth: boolean;
    requiredProviders: NotebookAuthProviderDto[];
    missingProviders: NotebookAuthProviderDto[];
} {
    if (!template || !template.authProviders) {
        return {
            needsAuth: false,
            requiredProviders: [],
            missingProviders: []
        };
    }

    // Any OAuth provider present in the template requires user authentication
    const requiredProviders = template.authProviders.filter(provider => {
        const isOAuth = provider.authType.toString().toLowerCase() === 'oauth';
        return isOAuth;
    });

    if (requiredProviders.length === 0) {
        return {
            needsAuth: false,
            requiredProviders: [],
            missingProviders: []
        };
    }

    const missingProviders = requiredProviders.filter(provider => {
        const tokenKey = `oauth_tokens_${projectId}_${provider.id}`;
        const tokenData = localStorage.getItem(tokenKey);
        
        if (!tokenData) {
            return true; // No token stored
        }

        try {
            const parsed = JSON.parse(tokenData);
            // Consider token as needing refresh if it expires within 5 minutes (300,000 ms)
            const fiveMinutesFromNow = Date.now() + (5 * 60 * 1000);
            return fiveMinutesFromNow >= parsed.expiresAt; // Token expired or expires soon
        } catch {
            return true; // Invalid token data
        }
    });

    return {
        needsAuth: missingProviders.length > 0,
        requiredProviders,
        missingProviders
    };
}

/**
 * Gets OAuth tokens for a specific provider if available and valid.
 * Returns null if token is expired or will expire within 5 minutes.
 */
export function getOAuthTokens(projectId: string, providerId: string): {
    accessToken: string;
    refreshToken?: string;
    expiresAt: number;
} | null {
    const tokenKey = `oauth_tokens_${projectId}_${providerId}`;
    const tokenData = localStorage.getItem(tokenKey);
    
    if (!tokenData) {
        return null;
    }

    try {
        const parsed = JSON.parse(tokenData);
        // Consider token as expired if it expires within 5 minutes (300,000 ms)
        const fiveMinutesFromNow = Date.now() + (5 * 60 * 1000);
        if (fiveMinutesFromNow >= parsed.expiresAt) {
            // Token expired or expires soon, clean it up
            localStorage.removeItem(tokenKey);
            return null;
        }
        
        return {
            accessToken: parsed.accessToken,
            refreshToken: parsed.refreshToken,
            expiresAt: parsed.expiresAt
        };
    } catch {
        // Invalid token data, clean it up
        localStorage.removeItem(tokenKey);
        return null;
    }
}

/**
 * Clears OAuth tokens for a specific provider.
 */
export function clearOAuthTokens(projectId: string, providerId: string): void {
    const tokenKey = `oauth_tokens_${projectId}_${providerId}`;
    localStorage.removeItem(tokenKey);
}

/**
 * Checks if a token needs refresh (expires within 5 minutes) without removing it.
 * This is useful for determining if we should trigger a refresh flow.
 */
export function tokenNeedsRefresh(projectId: string, providerId: string): boolean {
    const tokenKey = `oauth_tokens_${projectId}_${providerId}`;
    const tokenData = localStorage.getItem(tokenKey);
    
    if (!tokenData) {
        return true; // No token means it needs to be obtained
    }

    try {
        const parsed = JSON.parse(tokenData);
        // Check if token expires within 5 minutes (300,000 ms)
        const fiveMinutesFromNow = Date.now() + (5 * 60 * 1000);
        return fiveMinutesFromNow >= parsed.expiresAt;
    } catch {
        return true; // Invalid token data means it needs refresh
    }
}

/**
 * Stores OAuth tokens for a provider.
 */
export function storeOAuthTokens(
    projectId: string, 
    providerId: string, 
    tokens: {
        accessToken: string;
        refreshToken?: string;
        expiresAt: number;
    }
): void {
    const tokenKey = `oauth_tokens_${projectId}_${providerId}`;
    localStorage.setItem(tokenKey, JSON.stringify(tokens));
}

/**
 * Gets providers that need token refresh (expire within 5 minutes).
 */
export function getProvidersNeedingRefresh(
    template: NotebookTemplateDto | null,
    projectId: string
): NotebookAuthProviderDto[] {
    if (!template?.authProviders) {
        return [];
    }

    return template.authProviders.filter(provider => {
        const isOAuth = provider.authType.toString().toLowerCase() === 'oauth';
        if (!isOAuth) return false;
        
        return tokenNeedsRefresh(projectId, provider.id);
    });
}

/**
 * Attempts to refresh tokens for all providers that need refresh.
 * Returns the results of refresh attempts.
 */
export async function refreshTokensForTemplate(
    template: NotebookTemplateDto | null,
    projectId: string
): Promise<{
    refreshed: NotebookAuthProviderDto[];
    failed: NotebookAuthProviderDto[];
}> {
    const results = {
        refreshed: [] as NotebookAuthProviderDto[],
        failed: [] as NotebookAuthProviderDto[]
    };

    const providersNeedingRefresh = getProvidersNeedingRefresh(template, projectId);
    
    // Attempt to refresh each provider's tokens
    const refreshPromises = providersNeedingRefresh.map(async (provider) => {
        try {
            const success = await refreshOAuthTokens(projectId, provider.id, provider);
            if (success) {
                results.refreshed.push(provider);
            } else {
                results.failed.push(provider);
            }
        } catch (error) {
            console.error(`Failed to refresh tokens for provider ${provider.id}:`, error);
            results.failed.push(provider);
        }
    });

    await Promise.all(refreshPromises);
    return results;
}

/**
 * Ensures all OAuth tokens are valid and refreshed for a template.
 * Attempts to refresh tokens that expire within 5 minutes.
 * Returns auth requirements after refresh attempts.
 */
export async function ensureValidTokensForTemplate(
    template: NotebookTemplateDto | null,
    projectId: string
): Promise<{
    needsAuth: boolean;
    requiredProviders: NotebookAuthProviderDto[];
    missingProviders: NotebookAuthProviderDto[];
    refreshResults: {
        refreshed: NotebookAuthProviderDto[];
        failed: NotebookAuthProviderDto[];
    };
}> {
    // First, attempt to refresh any tokens that need it
    const refreshResults = await refreshTokensForTemplate(template, projectId);
    
    // After refresh attempts, check what's still needed
    const authRequirements = checkNotebookAuthRequirements(template, projectId);
    
    return {
        ...authRequirements,
        refreshResults
    };
}

/**
 * Collects all valid OAuth tokens for a notebook template's providers.
 * Returns a map of providerId -> accessToken for providers that have valid tokens.
 * Tokens that expire within 5 minutes are considered invalid and not included.
 */
export function collectOAuthTokensForTemplate(
    template: NotebookTemplateDto | null,
    projectId: string
): Record<string, string> {
    const tokens: Record<string, string> = {};
    
    if (!template?.authProviders) {
        return tokens;
    }

    template.authProviders.forEach(provider => {
        // Only collect OAuth tokens (not service_http)
        if (provider.authType.toString().toLowerCase() === 'oauth') {
            const tokenData = getOAuthTokens(projectId, provider.id);
            if (tokenData) {
                // Use the provider's valueTemplate if available, otherwise default to Bearer format
                const valueTemplate = provider.valueTemplate || 'Bearer {access_token}';
                const tokenValue = valueTemplate.replace('{access_token}', tokenData.accessToken);
                tokens[provider.id] = tokenValue;
            }
        }
    });

    return tokens;
}
