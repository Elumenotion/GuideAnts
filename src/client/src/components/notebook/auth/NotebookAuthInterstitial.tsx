import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { NotebookAuthProviderDto, NotebookTemplateDto } from '../../../types/project';
import { useToast } from '../../common/Toast';
import { resolveAgainstApiBase } from '../../../config/apiConfig';

interface NotebookAuthInterstitialProps {
    projectId: string;
    notebookId: string;
    notebookTitle: string;
    template: NotebookTemplateDto;
    onAuthComplete: () => void;
}

type ProviderAuthState = {
    provider: NotebookAuthProviderDto;
    isConnected: boolean;
    isRequired: boolean;
};

export function NotebookAuthInterstitial({ 
    projectId, 
    notebookId, 
    notebookTitle, 
    template, 
    onAuthComplete 
}: NotebookAuthInterstitialProps) {
    const navigate = useNavigate();
    const { showToast } = useToast();
    const [providerStates, setProviderStates] = useState<ProviderAuthState[]>([]);
    const [authenticating, setAuthenticating] = useState(false);

    // Check token status for all OAuth providers
    useEffect(() => {
        const checkTokens = () => {
            const providers = template.authProviders || [];
            const states: ProviderAuthState[] = [];

            providers.forEach(provider => {
                const policy = (provider.userConfigPolicy as unknown as string)?.toString().toLowerCase();
                const isRequired = policy === 'required';
                
                // Check all OAuth providers regardless of policy
                if (provider.authType.toString().toLowerCase() === 'oauth') {
                    const tokenKey = `oauth_tokens_${projectId}_${provider.id}`;
                    const tokenData = localStorage.getItem(tokenKey);
                    let isConnected = false;

                    if (tokenData) {
                        try {
                            const parsed = JSON.parse(tokenData);
                            isConnected = Date.now() < parsed.expiresAt;
                        } catch {
                            // Invalid token data
                        }
                    }

                    states.push({
                        provider,
                        isConnected,
                        isRequired
                    });
                }
            });

            setProviderStates(states);
        };

        checkTokens();
    }, [template, projectId]);

    // Check if we need to show the interstitial (any OAuth provider missing tokens)
    const providersNeedingAuth = providerStates.filter(state => 
        !state.isConnected
    );

    // If all required providers are authenticated, proceed to notebook
    useEffect(() => {
        if (providerStates.length > 0 && providersNeedingAuth.length === 0) {
            onAuthComplete();
        }
    }, [providerStates, providersNeedingAuth.length, onAuthComplete]);

    const handleSignIn = async (providerState: ProviderAuthState) => {
        setAuthenticating(true);
        
        try {
            const provider = providerState.provider;
            const effectiveClientId = provider.clientId;
            const effectiveTenant = provider.tenant;
            const scopes = provider.scopes || [];

            if (!effectiveClientId || !effectiveTenant) {
                showToast({
                    type: 'error',
                    title: 'Configuration Error',
                    message: 'OAuth configuration is incomplete. Please contact your project administrator.'
                });
                return;
            }

            // Generate PKCE parameters
            const codeVerifier = generateCodeVerifier();
            const codeChallenge = await generateCodeChallenge(codeVerifier);
            const state = crypto.randomUUID();
            
            // Store PKCE params in localStorage (persists across redirects)
            const pkceKey = `oauth_pkce_${projectId}_${provider.id}`;
            
            localStorage.setItem(pkceKey, JSON.stringify({
                codeVerifier,
                state,
                providerId: provider.id,
                projectId,
                clientId: effectiveClientId,
                tenant: effectiveTenant,
                scopes: scopes.join(' '),
                returnUrl: `/projects/${projectId}/notebooks/${notebookId}`
            }));

            // Build authorization URL
            const authUrl = new URL(`https://login.microsoftonline.com/${effectiveTenant}/oauth2/v2.0/authorize`);
            authUrl.searchParams.set('client_id', effectiveClientId);
            authUrl.searchParams.set('response_type', 'code');
            // Use same redirect URI logic as MSAL config
            let redirectUri = window.location.origin;
            if (window.location.origin.includes('localhost:3000')) {
                redirectUri = `${window.location.origin}/redirect`;
            } else {
                redirectUri = `${window.location.origin}/oauth/callback`;
            }
            authUrl.searchParams.set('redirect_uri', redirectUri);
            authUrl.searchParams.set('scope', scopes.join(' '));
            authUrl.searchParams.set('state', state);
            authUrl.searchParams.set('code_challenge', codeChallenge);
            authUrl.searchParams.set('code_challenge_method', 'S256');

            // Redirect to OAuth flow
            window.location.href = authUrl.toString();
            
        } catch (error) {
            console.error('OAuth initiation failed:', error);
            showToast({
                type: 'error',
                title: 'Authentication Error',
                message: 'Failed to start authentication. Please try again.'
            });
            setAuthenticating(false);
        }
    };

    // PKCE helper functions (same as ProjectGuideAuthContent)
    const generateCodeVerifier = (): string => {
        const array = new Uint8Array(32);
        crypto.getRandomValues(array);
        return btoa(String.fromCharCode.apply(null, Array.from(array)))
            .replace(/\+/g, '-')
            .replace(/\//g, '_')
            .replace(/=/g, '');
    };

    const generateCodeChallenge = async (verifier: string): Promise<string> => {
        const encoder = new TextEncoder();
        const data = encoder.encode(verifier);
        const digest = await crypto.subtle.digest('SHA-256', data);
        return btoa(String.fromCharCode.apply(null, Array.from(new Uint8Array(digest))))
            .replace(/\+/g, '-')
            .replace(/\//g, '_')
            .replace(/=/g, '');
    };

    const getProviderDisplayName = (providerId: string) => {
        // Convert provider ID to friendly name
        switch (providerId) {
            case 'graph.microsoft.com':
                return 'Microsoft 365';
            case 'api.github.com':
                return 'GitHub';
            default:
                return providerId;
        }
    };

    const getProviderDescription = (providerId: string) => {
        // Provide context for why each service is needed
        switch (providerId) {
            case 'graph.microsoft.com':
                return 'Access your emails, calendar, notes, and other Microsoft 365 data to provide personalized assistance';
            case 'api.github.com':
                return 'Access your repositories and code to provide development assistance';
            default:
                return 'Access external services to provide enhanced functionality';
        }
    };

    // Don't render if no providers need authentication
    if (providersNeedingAuth.length === 0) {
        return null;
    }

    return (
        <div className="h-screen w-full bg-gray-50 flex flex-col py-8 sm:px-6 lg:px-8 overflow-y-scroll">
            <div className="sm:mx-auto sm:w-full sm:max-w-3xl">
                <div className="text-center">
                    {template.avatarUrl && (() => {
                        let src = template.avatarUrl.startsWith('http')
                            ? template.avatarUrl
                            : resolveAgainstApiBase(template.avatarUrl).toString();
                        // Append projectId to avatar URL if it contains /api/ and doesn't already have projectId
                        if (src && src.includes('/api/') && !src.includes('projectId=')) {
                            src = `${src}${src.includes('?') ? '&' : '?'}projectId=${projectId}`;
                        }
                        return (
                            <img 
                                src={src}
                                alt={template.templateName}
                                className="mx-auto h-16 w-16 rounded-lg mb-4"
                            />
                        );
                    })()}
                    <h2 className="text-3xl font-bold tracking-tight text-gray-900">
                        Connect to External Services
                    </h2>
                    <p className="mt-2 text-sm text-gray-600">
                        The <strong>{notebookTitle}</strong> notebook requires access to external services to function properly.
                    </p>
                </div>
            </div>

            <div className="mt-6 sm:mx-auto sm:w-full sm:max-w-3xl">
                <div className="bg-white p-6 shadow sm:rounded-lg sm:p-10 overflow-visible">
                    <div className="space-y-6">
                        <div>
                            <h3 className="text-lg font-medium text-gray-900 mb-4">
                                Connections Needed
                            </h3>
                            <p className="text-sm text-gray-600 mb-6">
                                This guide (<strong>{template.templateName}</strong>) needs to connect to the following services to provide its full functionality. Your data will only be accessed when you explicitly use features that require it.
                            </p>
                        </div>

                        <div className="space-y-4">
                            {providersNeedingAuth.map((providerState) => (
                                <div key={providerState.provider.id} className="border border-gray-200 rounded-lg p-4">
                                    <div className="flex items-start justify-between">
                                        <div className="flex-1">
                                            <h4 className="text-base font-medium text-gray-900">
                                                {getProviderDisplayName(providerState.provider.id)}
                                            </h4>
                                            <p className="mt-1 text-sm text-gray-600">
                                                {getProviderDescription(providerState.provider.id)}
                                            </p>
                                            {providerState.provider.scopes && providerState.provider.scopes.length > 0 && (
                                                <div className="mt-2">
                                                    <p className="text-xs text-gray-500">
                                                        Permissions requested: {providerState.provider.scopes.join(', ')}
                                                    </p>
                                                </div>
                                            )}
                                        </div>
                                        <button
                                            onClick={() => handleSignIn(providerState)}
                                            disabled={authenticating}
                                            className="ml-4 inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-blue-600 hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500 disabled:opacity-50 disabled:cursor-not-allowed"
                                        >
                                            {authenticating ? (
                                                <>
                                                    <div className="animate-spin -ml-1 mr-2 h-4 w-4 border-2 border-white border-t-transparent rounded-full"></div>
                                                    Connecting...
                                                </>
                                            ) : (
                                                'Sign In'
                                            )}
                                        </button>
                                    </div>
                                </div>
                            ))}
                        </div>

                        <div className="mt-6 pt-6 border-t border-gray-200">
                            <div className="flex items-center justify-between">
                                <button
                                    onClick={() => navigate(`/projects/${projectId}`)}
                                    className="text-sm text-gray-600 hover:text-gray-900"
                                >
                                    ← Back to Project
                                </button>
                                <div className="text-xs text-gray-500">
                                    Secure authentication via OAuth 2.0
                                </div>
                            </div>
                        </div>

                        <div className="mt-4 p-4 bg-blue-50 rounded-md">
                            <div className="flex">
                                <div className="flex-shrink-0">
                                    <svg className="h-5 w-5 text-blue-400" viewBox="0 0 20 20" fill="currentColor">
                                        <path fillRule="evenodd" d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a1 1 0 000 2v3a1 1 0 001 1h1a1 1 0 100-2v-3a1 1 0 00-1-1H9z" clipRule="evenodd" />
                                    </svg>
                                </div>
                                <div className="ml-3">
                                    <h3 className="text-sm font-medium text-blue-800">
                                        Why do we need this?
                                    </h3>
                                    <div className="mt-2 text-sm text-blue-700">
                                        <p>
                                            This guide is designed to work with specific external services to provide you with personalized, intelligent assistance. 
                                            By connecting these services, the notebook can access your data to provide more relevant and helpful responses.
                                        </p>
                                        <p className="mt-2">
                                            <strong>Your privacy matters:</strong> We only access your data when you explicitly use features that require it, 
                                            and all connections use secure, industry-standard OAuth 2.0 authentication.
                                        </p>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    );
}
