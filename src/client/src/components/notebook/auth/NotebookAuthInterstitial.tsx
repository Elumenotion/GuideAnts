import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router';
import { NotebookAuthProviderDto, NotebookTemplateDto } from '../../../types/project';
import { useToast } from '../../common/Toast';
import { resolveAgainstApiBase } from '../../../config/apiConfig';
import { api } from '../../../services/api';
import { beginOAuthConnection } from '../../../utils/notebookAuth';

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

    // Check server-side OAuth connection status for all providers
    useEffect(() => {
        let cancelled = false;

        const checkStatuses = async () => {
            const providers = template.authProviders || [];
            const oauthProviders = providers.filter(provider =>
                provider.authType.toString().toLowerCase() === 'oauth');

            const states: ProviderAuthState[] = await Promise.all(
                oauthProviders.map(async provider => {
                    const policy = (provider.userConfigPolicy as unknown as string)?.toString().toLowerCase();
                    const isRequired = policy === 'required';

                    let isConnected = false;
                    try {
                        const status = await api.projects.externalAuth.oauth.status(projectId, provider.id);
                        isConnected = status.connected;
                    } catch {
                        isConnected = false;
                    }

                    return {
                        provider,
                        isConnected,
                        isRequired
                    };
                })
            );

            if (!cancelled) {
                setProviderStates(states);
            }
        };

        checkStatuses();
        return () => {
            cancelled = true;
        };
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
            await beginOAuthConnection(
                projectId,
                provider,
                `/projects/${projectId}/notebooks/${notebookId}`
            );
            
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
