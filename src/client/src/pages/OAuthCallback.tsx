import { useEffect } from 'react';
import { api } from '../services/api';
import { safeNavigate, buildUrl } from '../utils/urlSanitizer';
import { clearOAuthStateContext, readOAuthStateContext } from '../utils/notebookAuth';

export default function OAuthCallback() {
    useEffect(() => {
        const handleCallback = async () => {
            const urlParams = new URLSearchParams(window.location.search);
            const code = urlParams.get('code');
            const state = urlParams.get('state');
            const error = urlParams.get('error');
            const stateContext = state ? readOAuthStateContext(state) : null;

            const redirectToReturnUrl = (providerId?: string, oauthError?: string) => {
                const returnUrl = stateContext?.returnUrl || (stateContext ? `/projects/${stateContext.projectId}` : '/');
                const params = new URLSearchParams();

                if (providerId) {
                    params.set('oauthSuccess', providerId);
                }
                if (oauthError) {
                    params.set('oauthError', oauthError);
                }

                const finalUrl = buildUrl(returnUrl, params);
                safeNavigate(finalUrl, true);
            };

            if (error) {
                console.error('OAuth error:', error);
                redirectToReturnUrl(undefined, error);
                return;
            }

            if (!code || !state) {
                console.error('Missing OAuth parameters');
                const homeUrl = buildUrl('/');
                safeNavigate(homeUrl, true);
                return;
            }

            try {
                if (!stateContext) {
                    console.error('No matching PKCE session found for state:', state);
                    const homeUrl = buildUrl('/');
                    safeNavigate(homeUrl, true);
                    return;
                }

                await api.projects.externalAuth.oauth.callback(
                    stateContext.projectId,
                    stateContext.providerId,
                    { code, state }
                );

                redirectToReturnUrl(stateContext.providerId);

            } catch (error) {
                console.error('OAuth callback error:', error);
                redirectToReturnUrl(undefined, 'callback_failed');
            } finally {
                if (state) {
                    clearOAuthStateContext(state);
                }
            }
        };

        handleCallback();
    }, []);

    // Show minimal loading - this should execute and redirect immediately
    return (
        <div className="flex items-center justify-center min-h-screen">
            <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"></div>
        </div>
    );
}
