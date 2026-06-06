import { describe, it, expect } from 'vitest';
import { authFetchCredentials, authService, withAuthFetchInit } from '../authService';

describe('authService', () => {
  it('stores and returns the active account in memory', () => {
    authService.clearAuthState();
    authService.setActiveAccount({
      id: 'u1',
      name: 'User One',
      email: 'user.one@example.com',
      role: 'Contributor',
      mustChangePassword: false,
    });
    expect(authService.getActiveAccount()).toEqual({
      id: 'u1',
      name: 'User One',
      email: 'user.one@example.com',
      role: 'Contributor',
      mustChangePassword: false,
    });
  });

  it('initialize and signIn are compatibility no-ops', () => {
    expect(() => authService.initialize()).not.toThrow();
    expect(() => authService.signIn()).not.toThrow();
  });

  it('signOut clears session state', () => {
    authService.setActiveAccount({
      id: 'u1',
      name: 'User One',
      email: 'user.one@example.com',
      role: 'Contributor',
      mustChangePassword: false,
    });
    expect(() => authService.signOut()).not.toThrow();
    expect(authService.getActiveAccount()).toBeNull();
  });

  it('isReady returns true', () => {
    expect(authService.isReady()).toBe(true);
  });

  it('uses cookie credentials for authenticated fetch calls', () => {
    expect(authFetchCredentials).toBe('include');
    expect(withAuthFetchInit({ method: 'GET' })).toEqual({
      method: 'GET',
      credentials: 'include',
    });
  });
});
