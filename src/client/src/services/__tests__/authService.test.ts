import { describe, it, expect } from 'vitest';
import { authService } from '../authService';

describe('authService', () => {
  it('stores and returns access tokens', () => {
    authService.clearAuthState();
    authService.setAccessToken('token-123');
    expect(authService.getAccessToken()).toBe('token-123');
  });

  it('stores and returns the active account', () => {
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
    authService.setAccessToken('token-123');
    authService.setActiveAccount({
      id: 'u1',
      name: 'User One',
      email: 'user.one@example.com',
      role: 'Contributor',
      mustChangePassword: false,
    });
    expect(() => authService.signOut()).not.toThrow();
    expect(authService.getAccessToken()).toBeNull();
    expect(authService.getActiveAccount()).toBeNull();
  });

  it('isReady returns true', () => {
    expect(authService.isReady()).toBe(true);
  });
});
