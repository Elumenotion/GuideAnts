using Microsoft.Identity.Client;
using System.Collections.Concurrent;
using System.Linq;

namespace AntRunner.ToolCalling.Identity
{
    /// <summary>
    /// Gets an OAuth token for a given client ID and tenant ID.
    /// </summary>
    public class OAuthHelper
    {
        class CachedToken
        {
            public string AccessToken { get; init; } = string.Empty;
            public DateTimeOffset ExpiresOn { get; init; }
        }

        private static readonly ConcurrentDictionary<string, CachedToken> CachedTokens = new();

        /// <summary>
        /// Gets an OAuth token for a given client ID and tenant ID.
        /// </summary>
        /// <param name="clientId">The client ID, a unique identifier assigned to the client application.</param>
        /// <param name="tenantId">The tenant ID, which identifies the organization or tenant.</param>
        /// <param name="scopes">The permissions or scopes that the client application is requesting.</param>
        /// <param name="redirectUri">The redirect URI where the authorization server will redirect the user after authentication. Default value is "http://localhost".</param>
        /// <returns>The OAuth token as a string.</returns>
        public static async Task<string> GetToken(string clientId, string tenantId, string[] scopes, string? redirectUri = "http://localhost")
        {
            // Check if the token is already in the cache
            if (CachedTokens.TryGetValue(clientId, out var cachedToken))
            {
                if (DateTimeOffset.UtcNow < cachedToken.ExpiresOn)
                {
                    return $"Bearer {cachedToken.AccessToken}";
                }
            }

            var authority = $"https://login.microsoftonline.com/{tenantId}";
            var app = PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority(authority)
                .WithRedirectUri(redirectUri)
                .Build();

            AuthenticationResult? result = null;
            var accounts = await app.GetAccountsAsync();

            if (accounts.Any())
            {
                try
                {
                    // Try to acquire a token silently
                    result = await app.AcquireTokenSilent(scopes, accounts.FirstOrDefault())
                        .ExecuteAsync();
                }
                catch (MsalUiRequiredException)
                {
                    // Fallback to interactive sign-in if silent acquisition fails
                    result = await app.AcquireTokenInteractive(scopes)
                        .WithPrompt(Prompt.SelectAccount)
                        .ExecuteAsync();
                }
            }
            else
            {
                // Perform an interactive sign-in if no accounts are found in cache
                result = await app.AcquireTokenInteractive(scopes)
                    .WithPrompt(Prompt.SelectAccount)
                    .ExecuteAsync();
            }

            // Add the token to the cache
            CachedTokens[clientId] = new() { AccessToken = result.AccessToken, ExpiresOn = result.ExpiresOn };

            return $"Bearer {result.AccessToken}";
        }
    }
}
