import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { NotebookAuthProviderDto, NotebookTemplateDto } from '../../types/project';

const mockAuthorizeUrl = vi.fn();
const mockOAuthStatus = vi.fn();

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      externalAuth: {
        oauth: {
          authorizeUrl: (...args: unknown[]) => mockAuthorizeUrl(...args),
          status: (...args: unknown[]) => mockOAuthStatus(...args),
        },
      },
    },
  },
}));

import {
  getOAuthRedirectUri,
  saveOAuthStateContext,
  readOAuthStateContext,
  clearOAuthStateContext,
  beginOAuthConnection,
  checkNotebookAuthRequirements,
  ensureValidTokensForTemplate,
} from '../notebookAuth';

const OAUTH_WINDOW_NAME_PREFIX = '__guideants_oauth_state__';

function makeProvider(overrides: Partial<NotebookAuthProviderDto> = {}): NotebookAuthProviderDto {
  return {
    id: 'provider-1',
    authType: 'oauth',
    clientId: 'client-id',
    scopes: ['scope.read'],
    tenant: 'organizations',
    userConfigPolicy: 'optional',
    ...overrides,
  };
}

function makeTemplate(authProviders?: NotebookAuthProviderDto[]): NotebookTemplateDto {
  return {
    id: 'template-1',
    templateName: 'Test',
    description: '',
    avatarUrl: '',
    conversationStarters: [],
    authProviders,
  };
}

describe('notebookAuth', () => {
  const originalLocation = window.location;

  beforeEach(() => {
    vi.clearAllMocks();
    window.name = '';
    delete (window as { location?: Location }).location;
  });

  afterEach(() => {
    window.name = '';
    Object.defineProperty(window, 'location', {
      value: originalLocation,
      writable: true,
      configurable: true,
    });
  });

  function setLocationOrigin(origin: string) {
    Object.defineProperty(window, 'location', {
      value: { origin, href: origin },
      writable: true,
      configurable: true,
    });
  }

  describe('getOAuthRedirectUri', () => {
    it('returns /redirect for localhost:3000', () => {
      setLocationOrigin('http://localhost:3000');
      expect(getOAuthRedirectUri()).toBe('http://localhost:3000/redirect');
    });

    it('returns /oauth/callback for production origins', () => {
      setLocationOrigin('https://app.guideants.com');
      expect(getOAuthRedirectUri()).toBe('https://app.guideants.com/oauth/callback');
    });
  });

  describe('OAuth state via window.name', () => {
    it('saves and reads OAuth state context', () => {
      saveOAuthStateContext('state-abc', {
        projectId: 'proj-1',
        providerId: 'provider-1',
        returnUrl: '/back',
      });

      expect(window.name.startsWith(OAUTH_WINDOW_NAME_PREFIX)).toBe(true);
      expect(readOAuthStateContext('state-abc')).toEqual({
        projectId: 'proj-1',
        providerId: 'provider-1',
        returnUrl: '/back',
      });
    });

    it('returns null for missing or empty state', () => {
      expect(readOAuthStateContext('')).toBeNull();
      expect(readOAuthStateContext('missing')).toBeNull();
    });

    it('no-ops save/clear for empty state', () => {
      saveOAuthStateContext('', { projectId: 'p', providerId: 'pr' });
      expect(window.name).toBe('');

      saveOAuthStateContext('state-1', { projectId: 'p', providerId: 'pr' });
      clearOAuthStateContext('');
      expect(readOAuthStateContext('state-1')).not.toBeNull();
    });

    it('clears OAuth state and resets window.name when store is empty', () => {
      saveOAuthStateContext('state-1', { projectId: 'p', providerId: 'pr' });
      clearOAuthStateContext('state-1');
      expect(readOAuthStateContext('state-1')).toBeNull();
      expect(window.name).toBe('');
    });

    it('ignores malformed window.name payload', () => {
      window.name = `${OAUTH_WINDOW_NAME_PREFIX}not-json`;
      expect(readOAuthStateContext('state-1')).toBeNull();
    });

    it('no-ops clear when state is not in store', () => {
      saveOAuthStateContext('state-1', { projectId: 'p', providerId: 'pr' });
      const before = window.name;
      clearOAuthStateContext('state-other');
      expect(window.name).toBe(before);
    });
  });

  describe('beginOAuthConnection', () => {
    it('throws when client ID is missing', async () => {
      await expect(
        beginOAuthConnection('proj-1', makeProvider({ clientId: '  ' }))
      ).rejects.toThrow('OAuth client ID is not configured.');
    });

    it('throws when scopes are missing', async () => {
      await expect(
        beginOAuthConnection('proj-1', makeProvider({ scopes: [] }))
      ).rejects.toThrow('OAuth scopes are not configured.');
    });

    it('calls authorizeUrl, saves state, and redirects on success', async () => {
      setLocationOrigin('https://app.guideants.com');
      mockAuthorizeUrl.mockResolvedValue({
        authorizeUrl: 'https://login.example.com/oauth',
        state: 'oauth-state-123',
        expiresAt: '2026-01-01T00:00:00Z',
      });

      await beginOAuthConnection('proj-1', makeProvider({ tenant: '' }), '/return');

      expect(mockAuthorizeUrl).toHaveBeenCalledWith('proj-1', 'provider-1', {
        clientId: 'client-id',
        tenant: 'organizations',
        scopes: ['scope.read'],
        redirectUri: 'https://app.guideants.com/oauth/callback',
        returnUrl: '/return',
      });
      expect(readOAuthStateContext('oauth-state-123')).toEqual({
        projectId: 'proj-1',
        providerId: 'provider-1',
        returnUrl: '/return',
      });
      expect(window.location.href).toBe('https://login.example.com/oauth');
    });
  });

  describe('checkNotebookAuthRequirements', () => {
    it('returns no auth needed when template is null', async () => {
      const result = await checkNotebookAuthRequirements(null, 'proj-1');
      expect(result).toEqual({
        needsAuth: false,
        requiredProviders: [],
        missingProviders: [],
      });
    });

    it('returns no auth needed when no oauth providers', async () => {
      const template = makeTemplate([
        makeProvider({ authType: 'service_http' }),
      ]);
      const result = await checkNotebookAuthRequirements(template, 'proj-1');
      expect(result).toEqual({
        needsAuth: false,
        requiredProviders: [],
        missingProviders: [],
      });
    });

    it('identifies missing oauth providers', async () => {
      const provider = makeProvider();
      mockOAuthStatus.mockResolvedValueOnce({ connected: false });

      const result = await checkNotebookAuthRequirements(makeTemplate([provider]), 'proj-1');

      expect(mockOAuthStatus).toHaveBeenCalledWith('proj-1', 'provider-1');
      expect(result.needsAuth).toBe(true);
      expect(result.requiredProviders).toEqual([provider]);
      expect(result.missingProviders).toEqual([provider]);
    });

    it('treats status errors as disconnected', async () => {
      const provider = makeProvider();
      mockOAuthStatus.mockRejectedValueOnce(new Error('network'));

      const result = await checkNotebookAuthRequirements(makeTemplate([provider]), 'proj-1');

      expect(result.needsAuth).toBe(true);
      expect(result.missingProviders).toEqual([provider]);
    });

    it('returns no auth needed when all oauth providers are connected', async () => {
      const provider = makeProvider();
      mockOAuthStatus.mockResolvedValueOnce({ connected: true });

      const result = await checkNotebookAuthRequirements(makeTemplate([provider]), 'proj-1');

      expect(result).toEqual({
        needsAuth: false,
        requiredProviders: [provider],
        missingProviders: [],
      });
    });
  });

  describe('ensureValidTokensForTemplate', () => {
    it('delegates to checkNotebookAuthRequirements and adds empty refresh results', async () => {
      const provider = makeProvider();
      mockOAuthStatus.mockResolvedValueOnce({ connected: true });

      const result = await ensureValidTokensForTemplate(makeTemplate([provider]), 'proj-1');

      expect(result).toEqual({
        needsAuth: false,
        requiredProviders: [provider],
        missingProviders: [],
        refreshResults: { refreshed: [], failed: [] },
      });
    });
  });
});
