import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import type { ChangePasswordRequest, LoginRequest, RegisterRequest } from '../types/auth';
import type { AppRole } from '../types/user';
import { api } from '../services/api';
import { authService, type AuthAccount } from '../services/authService';

export type AuthStatus = 'loading' | 'anonymous' | 'authenticated';

export interface AuthUser {
  id: string;
  name: string;
  email: string;
  role: AppRole;
  mustChangePassword: boolean;
  lastLoginAt?: string | null;
}

interface AuthContextValue {
  user: AuthUser | null;
  role: AppRole | null;
  status: AuthStatus;
  isAuthenticated: boolean;
  login: (request: LoginRequest) => Promise<AuthUser>;
  register: (request: RegisterRequest) => Promise<AuthUser>;
  changePassword: (request: ChangePasswordRequest) => Promise<void>;
  refresh: () => Promise<AuthUser | null>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

function accountToUser(account: AuthAccount | null): AuthUser | null {
  if (!account) {
    return null;
  }

  return {
    id: account.id,
    name: account.name,
    email: account.email,
    role: account.role,
    mustChangePassword: account.mustChangePassword,
    lastLoginAt: null,
  };
}

function persistSession(user: AuthUser): void {
  authService.setActiveAccount({
    id: user.id,
    name: user.name,
    email: user.email,
    role: user.role,
    mustChangePassword: user.mustChangePassword,
  });
}

function clearSession(): void {
  authService.clearAuthState();
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(() => accountToUser(authService.getActiveAccount()));
  const [status, setStatus] = useState<AuthStatus>('loading');

  const logout = useCallback(() => {
    void api.auth.logout().catch(() => {
      // Local session should still clear when the server logout call fails.
    });
    clearSession();
    setUser(null);
    setStatus('anonymous');
  }, []);

  const refresh = useCallback(async (): Promise<AuthUser | null> => {
    try {
      const me = await api.auth.me();
      const refreshedUser: AuthUser = {
        id: me.userId,
        name: me.name,
        email: me.email,
        role: me.role,
        mustChangePassword: me.mustChangePassword,
        lastLoginAt: me.lastLoginAt ?? null,
      };
      persistSession(refreshedUser);
      setUser(refreshedUser);
      setStatus('authenticated');
      return refreshedUser;
    } catch {
      clearSession();
      setUser(null);
      setStatus('anonymous');
      return null;
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const login = useCallback(async (request: LoginRequest): Promise<AuthUser> => {
    const response = await api.auth.login(request);
    const authenticatedUser: AuthUser = {
      id: response.userId,
      name: response.name,
      email: response.email,
      role: response.role,
      mustChangePassword: response.mustChangePassword,
      lastLoginAt: null,
    };
    persistSession(authenticatedUser);
    setUser(authenticatedUser);
    setStatus('authenticated');
    return authenticatedUser;
  }, []);

  const register = useCallback(async (request: RegisterRequest): Promise<AuthUser> => {
    const response = await api.auth.register(request);
    const authenticatedUser: AuthUser = {
      id: response.userId,
      name: response.name,
      email: response.email,
      role: response.role,
      mustChangePassword: response.mustChangePassword,
      lastLoginAt: null,
    };
    persistSession(authenticatedUser);
    setUser(authenticatedUser);
    setStatus('authenticated');
    return authenticatedUser;
  }, []);

  const changePassword = useCallback(async (request: ChangePasswordRequest): Promise<void> => {
    await api.auth.changePassword(request);
    const refreshed = await refresh();
    if (refreshed) {
      setUser((previous) => ({
        ...(previous ?? refreshed),
        mustChangePassword: false,
      }));
    }
  }, [refresh]);

  const value = useMemo<AuthContextValue>(() => ({
    user,
    role: user?.role ?? null,
    status,
    isAuthenticated: status === 'authenticated' && user !== null,
    login,
    register,
    changePassword,
    refresh,
    logout,
  }), [user, status, login, register, changePassword, refresh, logout]);

  return (
    <AuthContext.Provider value={value}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used within AuthProvider.');
  }
  return context;
}
