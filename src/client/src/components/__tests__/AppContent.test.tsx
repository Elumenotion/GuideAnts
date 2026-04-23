import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../test/test-utils';
import { useMsal, useIsAuthenticated } from '@azure/msal-react';

// Add additional page mocks before AppContent import
vi.mock('../../pages/NewProject', () => ({ default: () => <div>New Project Page</div> }));
vi.mock('../../pages/EditProject', () => ({ default: () => <div>Edit Project Page</div> }));
vi.mock('../../pages/ProjectDetails', () => ({ default: () => <div>Project Details Page</div> }));
vi.mock('../../pages/Projects', () => ({ default: () => <div>Projects Page</div> }));
vi.mock('../../pages/Login', () => ({ default: () => <div>Login Page</div> }));

// Reuse existing mock from test-utils but extend behaviour per test
vi.mock('@azure/msal-react', async () => {
  const actual = await vi.importActual('@azure/msal-react');
  return {
    ...actual,
    useMsal: vi.fn(),
    useIsAuthenticated: vi.fn(),
  };
});

// Mock authService to avoid crypto errors from msal-browser
vi.mock('../services/authService', () => ({
  authService: {
    signIn: vi.fn(),
    getAccessToken: vi.fn(),
    initialize: vi.fn(),
    getMsalInstance: vi.fn(),
  }
}));

// Mock lightweight msal-browser constants used by AppContent
vi.mock('@azure/msal-browser', () => {
  class PublicClientApplicationMock {
    initialize = vi.fn().mockResolvedValue(undefined);
    handleRedirectPromise = vi.fn().mockResolvedValue(null);
    getAllAccounts = vi.fn().mockReturnValue([]);
    setActiveAccount = vi.fn();
    acquireTokenSilent = vi.fn().mockResolvedValue({ accessToken: 'token' });
    acquireTokenRedirect = vi.fn().mockResolvedValue(undefined);
    loginRedirect = vi.fn().mockResolvedValue(undefined);
    logoutRedirect = vi.fn().mockResolvedValue(undefined);
  }

  return {
    PublicClientApplication: PublicClientApplicationMock,
    InteractionStatus: { None: 'None', Login: 'Login' },
    EventType: { LOGIN_SUCCESS: 'LOGIN_SUCCESS' },
    LogLevel: { Verbose: 'Verbose' }
  };
});

// Import after mocks so that the component picks up the mocked dependencies
import AppContent from '../AppContent';

describe('AppContent', () => {
  const mockInstance = {
    handleRedirectPromise: vi.fn().mockResolvedValue(null),
    logoutRedirect: vi.fn().mockResolvedValue(null),
    getAllAccounts: vi.fn().mockReturnValue([]),
    addEventCallback: vi.fn().mockReturnValue('callback-id'),
    removeEventCallback: vi.fn(),
  } as any;

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders loading spinner while auth interaction is in progress', () => {
    (useMsal as any).mockReturnValue({ instance: mockInstance, inProgress: 'Login' });
    render(<AppContent />);
    expect(screen.getByText('Loading your content...')).toBeInTheDocument();
  });

  it('renders login page when route is /login and auth interaction is none', () => {
    (useMsal as any).mockReturnValue({ instance: mockInstance, inProgress: 'None', accounts: [] });
    (useIsAuthenticated as any).mockReturnValue(false);

    render(<AppContent />, { initialRoute: '/login' });
    expect(screen.getByText('Login Page')).toBeInTheDocument();
  });
}); 