import { useEffect } from 'react';
import { getRouterType } from '../utils/environment';
import { safeNavigate, buildUrl } from '../utils/urlSanitizer';

export default function OAuthCallback() {
    const routerType = getRouterType();

    useEffect(() => {
        const handleCallback = async () => {
            const urlParams = new URLSearchParams(window.location.search);
            const code = urlParams.get('code');
            const state = urlParams.get('state');
            const error = urlParams.get('error');

            if (error) {
                console.error('OAuth error:', error);
                const homeUrl = buildUrl('/');
                safeNavigate(homeUrl, true);
                return;
            }

            if (!code || !state) {
                console.error('Missing OAuth parameters');
                const homeUrl = buildUrl('/');
                safeNavigate(homeUrl, true);
                return;
            }

            try {
                // Debug: log what we're looking for and what's in storage
                console.log('OAuth callback - looking for state:', state);
                console.log('LocalStorage keys:', Array.from({length: localStorage.length}, (_, i) => localStorage.key(i)));
                
                // Find PKCE session by iterating and matching state
                let pkceData = null;
                let foundKey = null;
                
                for (let i = 0; i < localStorage.length; i++) {
                    const key = localStorage.key(i);
                    if (key?.startsWith('oauth_pkce_')) {
                        try {
                            const data = JSON.parse(localStorage.getItem(key) || '{}');
                            console.log(`Checking key ${key}, stored state: ${data.state}`);
                            if (data.state === state) {
                                pkceData = data;
                                foundKey = key;
                                console.log('Found matching PKCE session:', key);
                                break;
                            }
                        } catch (e) {
                            console.warn('Failed to parse PKCE data for key:', key, e);
                        }
                    }
                }

                if (!pkceData || !foundKey) {
                    console.error('No matching PKCE session found for state:', state);
                    console.error('Available PKCE keys:', Array.from({length: localStorage.length}, (_, i) => localStorage.key(i)).filter(k => k?.startsWith('oauth_pkce_')));
                    const homeUrl = buildUrl('/');
                    safeNavigate(homeUrl, true);
                    return;
                }

                // Clean up the PKCE session
                localStorage.removeItem(foundKey);

                // Exchange code for tokens
                // Use same redirect URI logic as MSAL config and OAuth flows
                let redirectUri = window.location.origin;
                if (window.location.origin.includes('localhost:3000')) {
                    redirectUri = `${window.location.origin}/redirect`;
                } else {
                    redirectUri = `${window.location.origin}/oauth/callback`;
                }
                
                const tokenResponse = await fetch(`https://login.microsoftonline.com/${pkceData.tenant || 'organizations'}/oauth2/v2.0/token`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body: new URLSearchParams({
                        client_id: pkceData.clientId,
                        scope: pkceData.scopes,
                        code,
                        redirect_uri: redirectUri,
                        grant_type: 'authorization_code',
                        code_verifier: pkceData.codeVerifier
                    })
                });

                if (!tokenResponse.ok) {
                    throw new Error(`Token exchange failed: ${tokenResponse.statusText}`);
                }

                const tokens = await tokenResponse.json();
                
                // Store tokens with project+provider key
                const tokenKey = `oauth_tokens_${pkceData.projectId}_${pkceData.providerId}`;
                localStorage.setItem(tokenKey, JSON.stringify({
                    accessToken: tokens.access_token,
                    refreshToken: tokens.refresh_token,
                    expiresAt: Date.now() + (tokens.expires_in * 1000),
                    scope: tokens.scope,
                    providerId: pkceData.providerId,
                    projectId: pkceData.projectId,
                    created: Date.now()
                }));

                console.log(`OAuth tokens stored for ${pkceData.providerId} in project ${pkceData.projectId}`);
                
                // Navigate back to where user started with success indicator
                const returnUrl = pkceData.returnUrl || `/projects/${pkceData.projectId}`;
                const params = new URLSearchParams();
                params.set('oauthSuccess', pkceData.providerId);
                
                // Use safe navigation with proper URL construction
                const finalUrl = buildUrl(returnUrl, params);
                safeNavigate(finalUrl, true);

            } catch (error) {
                console.error('OAuth callback error:', error);
                // Try to get return URL from any remaining PKCE session
                let returnUrl = '/';
                try {
                    // Look for any remaining PKCE session to get return URL
                    for (let i = 0; i < localStorage.length; i++) {
                        const key = localStorage.key(i);
                        if (key?.startsWith('oauth_pkce_')) {
                            const data = JSON.parse(localStorage.getItem(key) || '{}');
                            returnUrl = data.returnUrl || '/';
                            localStorage.removeItem(key);
                            break;
                        }
                    }
                } catch {}
                
                // Use safe navigation for error cases too
                const finalUrl = buildUrl(returnUrl);
                safeNavigate(finalUrl, true);
            }
        };

        handleCallback();
    }, [routerType]);

    // Show minimal loading - this should execute and redirect immediately
    return (
        <div className="flex items-center justify-center min-h-screen">
            <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"></div>
        </div>
    );
}
