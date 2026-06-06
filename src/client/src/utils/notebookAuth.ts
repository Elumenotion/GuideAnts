import { api } from '../services/api';
import { NotebookAuthProviderDto, NotebookTemplateDto } from '../types/project';

const OAUTH_WINDOW_NAME_PREFIX = '__guideants_oauth_state__';

export interface OAuthStateSessionContext {
    projectId: string;
    providerId: string;
    returnUrl?: string;
}

export function getOAuthRedirectUri(): string {
    if (window.location.origin.includes('localhost:3000')) {
        return `${window.location.origin}/redirect`;
    }

    return `${window.location.origin}/oauth/callback`;
}

export function saveOAuthStateContext(state: string, context: OAuthStateSessionContext): void {
    if (!state) return;
    const store = readWindowNameStore();
    store[state] = context;
    writeWindowNameStore(store);
}

export function readOAuthStateContext(state: string): OAuthStateSessionContext | null {
    if (!state) return null;
    const store = readWindowNameStore();
    return store[state] ?? null;
}

export function clearOAuthStateContext(state: string): void {
    if (!state) return;
    const store = readWindowNameStore();
    if (!(state in store)) {
        return;
    }

    delete store[state];
    writeWindowNameStore(store);
}

export async function beginOAuthConnection(
    projectId: string,
    provider: NotebookAuthProviderDto,
    returnUrl?: string
): Promise<void> {
    const clientId = provider.clientId?.trim();
    if (!clientId) {
        throw new Error('OAuth client ID is not configured.');
    }

    const tenant = provider.tenant?.trim() || 'organizations';
    const scopes = (provider.scopes || []).filter(scope => !!scope && scope.trim().length > 0);
    if (scopes.length === 0) {
        throw new Error('OAuth scopes are not configured.');
    }

    const redirectUri = getOAuthRedirectUri();
    const result = await api.projects.externalAuth.oauth.authorizeUrl(
        projectId,
        provider.id,
        {
            clientId,
            tenant,
            scopes,
            redirectUri,
            returnUrl,
        }
    );

    saveOAuthStateContext(result.state, {
        projectId,
        providerId: provider.id,
        returnUrl,
    });

    window.location.href = result.authorizeUrl;
}

export async function checkNotebookAuthRequirements(
    template: NotebookTemplateDto | null,
    projectId: string
): Promise<{
    needsAuth: boolean;
    requiredProviders: NotebookAuthProviderDto[];
    missingProviders: NotebookAuthProviderDto[];
}> {
    if (!template?.authProviders) {
        return {
            needsAuth: false,
            requiredProviders: [],
            missingProviders: [],
        };
    }

    const requiredProviders = template.authProviders.filter(provider =>
        provider.authType.toString().toLowerCase() === 'oauth');

    if (requiredProviders.length === 0) {
        return {
            needsAuth: false,
            requiredProviders: [],
            missingProviders: [],
        };
    }

    const statuses = await Promise.all(
        requiredProviders.map(async provider => {
            try {
                const status = await api.projects.externalAuth.oauth.status(projectId, provider.id);
                return { provider, connected: status.connected };
            } catch {
                return { provider, connected: false };
            }
        })
    );

    const missingProviders = statuses
        .filter(item => !item.connected)
        .map(item => item.provider);

    return {
        needsAuth: missingProviders.length > 0,
        requiredProviders,
        missingProviders,
    };
}

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
    const requirements = await checkNotebookAuthRequirements(template, projectId);

    return {
        ...requirements,
        refreshResults: {
            refreshed: [],
            failed: [],
        },
    };
}

function readWindowNameStore(): Record<string, OAuthStateSessionContext> {
    const raw = window.name;
    if (!raw || !raw.startsWith(OAUTH_WINDOW_NAME_PREFIX)) {
        return {};
    }

    const payload = raw.slice(OAUTH_WINDOW_NAME_PREFIX.length);
    if (!payload) {
        return {};
    }

    try {
        const parsed = JSON.parse(payload);
        if (parsed && typeof parsed === 'object') {
            return parsed as Record<string, OAuthStateSessionContext>;
        }
    } catch {
        // Ignore malformed window.name payload.
    }

    return {};
}

function writeWindowNameStore(store: Record<string, OAuthStateSessionContext>): void {
    const keys = Object.keys(store);
    if (keys.length === 0) {
        if (window.name.startsWith(OAUTH_WINDOW_NAME_PREFIX)) {
            window.name = '';
        }
        return;
    }

    window.name = `${OAUTH_WINDOW_NAME_PREFIX}${JSON.stringify(store)}`;
}
