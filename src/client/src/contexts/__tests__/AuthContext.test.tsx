import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { AuthProvider, useAuth } from '../AuthContext';
import { api } from '../../services/api';
import { authService } from '../../services/authService';

const mockGetActiveAccount = vi.fn();
const mockSetActiveAccount = vi.fn();
const mockClearAuthState = vi.fn();

vi.mock('../../services/authService', () => ({
  authService: {
    getActiveAccount: () => mockGetActiveAccount(),
    setActiveAccount: (account: unknown) => mockSetActiveAccount(account),
    clearAuthState: () => mockClearAuthState(),
  },
}));

vi.mock('../../services/api', () => ({
  api: {
    auth: {
      me: vi.fn(),
      login: vi.fn(),
      register: vi.fn(),
      logout: vi.fn(),
      changePassword: vi.fn(),
    },
  },
}));

const mockApi = api as unknown as {
  auth: {
    me: ReturnType<typeof vi.fn>;
    login: ReturnType<typeof vi.fn>;
    register: ReturnType<typeof vi.fn>;
    logout: ReturnType<typeof vi.fn>;
    changePassword: ReturnType<typeof vi.fn>;
  };
};

const storedUser = {
  id: 'user-1',
  name: 'Stored User',
  email: 'stored@example.com',
  role: 'User' as const,
  mustChangePassword: false,
};

const meResponse = {
  userId: 'user-1',
  name: 'Live User',
  email: 'live@example.com',
  role: 'Admin' as const,
  mustChangePassword: true,
  lastLoginAt: '2026-01-01T00:00:00Z',
};

function wrapper({ children }: { children: React.ReactNode }) {
  return <AuthProvider>{children}</AuthProvider>;
}

describe('AuthContext', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGetActiveAccount.mockReturnValue(null);
    mockApi.auth.me.mockResolvedValue(meResponse);
    mockApi.auth.logout.mockResolvedValue(undefined);
  });

  it('throws when useAuth is used outside AuthProvider', () => {
    expect(() => renderHook(() => useAuth())).toThrow('useAuth must be used within AuthProvider.');
  });

  it('hydrates user from active account before refresh completes', () => {
    mockGetActiveAccount.mockReturnValue(storedUser);

    const { result } = renderHook(() => useAuth(), { wrapper });

    expect(result.current.user).toEqual({
      ...storedUser,
      lastLoginAt: null,
    });
  });

  it('refresh loads authenticated user from api.auth.me', async () => {
    const { result } = renderHook(() => useAuth(), { wrapper });

    await waitFor(() => {
      expect(result.current.status).toBe('authenticated');
    });

    expect(mockApi.auth.me).toHaveBeenCalled();
    expect(result.current.user).toEqual({
      id: meResponse.userId,
      name: meResponse.name,
      email: meResponse.email,
      role: meResponse.role,
      mustChangePassword: meResponse.mustChangePassword,
      lastLoginAt: meResponse.lastLoginAt,
    });
    expect(result.current.isAuthenticated).toBe(true);
    expect(mockSetActiveAccount).toHaveBeenCalled();
  });

  it('refresh clears session when api.auth.me fails', async () => {
    mockApi.auth.me.mockRejectedValue(new Error('unauthorized'));

    const { result } = renderHook(() => useAuth(), { wrapper });

    await waitFor(() => {
      expect(result.current.status).toBe('anonymous');
    });

    expect(result.current.user).toBeNull();
    expect(result.current.isAuthenticated).toBe(false);
    expect(mockClearAuthState).toHaveBeenCalled();
  });

  it('login persists session and marks user authenticated', async () => {
    mockApi.auth.login.mockResolvedValue({
      userId: 'new-user',
      name: 'New User',
      email: 'new@example.com',
      role: 'User',
      mustChangePassword: false,
    });

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.status).toBe('authenticated'));

    let loggedInUser: unknown;
    await act(async () => {
      loggedInUser = await result.current.login({ email: 'new@example.com', password: 'secret' });
    });

    expect(loggedInUser).toEqual({
      id: 'new-user',
      name: 'New User',
      email: 'new@example.com',
      role: 'User',
      mustChangePassword: false,
      lastLoginAt: null,
    });
    expect(mockSetActiveAccount).toHaveBeenCalled();
    expect(result.current.isAuthenticated).toBe(true);
  });

  it('register persists session and marks user authenticated', async () => {
    mockApi.auth.register.mockResolvedValue({
      userId: 'reg-user',
      name: 'Registered',
      email: 'reg@example.com',
      role: 'User',
      mustChangePassword: true,
    });

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.status).toBe('authenticated'));

    await act(async () => {
      await result.current.register({
        name: 'Registered',
        email: 'reg@example.com',
        password: 'secret',
      });
    });

    expect(result.current.user?.email).toBe('reg@example.com');
    expect(result.current.status).toBe('authenticated');
    expect(mockSetActiveAccount).toHaveBeenCalled();
  });

  it('logout clears local session even when server logout fails', async () => {
    mockApi.auth.logout.mockRejectedValue(new Error('network'));
    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.status).toBe('authenticated'));

    act(() => {
      result.current.logout();
    });

    expect(result.current.user).toBeNull();
    expect(result.current.status).toBe('anonymous');
    expect(mockClearAuthState).toHaveBeenCalled();
  });

  it('changePassword refreshes user and clears mustChangePassword flag', async () => {
    mockApi.auth.changePassword.mockResolvedValue(undefined);
    mockApi.auth.me.mockResolvedValue({
      ...meResponse,
      mustChangePassword: true,
    });

    const { result } = renderHook(() => useAuth(), { wrapper });
    await waitFor(() => expect(result.current.user?.mustChangePassword).toBe(true));

    await act(async () => {
      await result.current.changePassword({
        currentPassword: 'old',
        newPassword: 'new-password',
      });
    });

    expect(mockApi.auth.changePassword).toHaveBeenCalled();
    expect(result.current.user?.mustChangePassword).toBe(false);
  });

  it('exposes role from authenticated user', async () => {
    const { result } = renderHook(() => useAuth(), { wrapper });

    await waitFor(() => {
      expect(result.current.role).toBe('Admin');
    });
  });
});
