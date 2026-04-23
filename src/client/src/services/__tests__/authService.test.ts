import { describe, it, expect } from 'vitest';
import { authService } from '../authService';

describe('authService (OSS-lite stub)', () => {
  it('getAccessToken returns the stub token', () => {
    expect(authService.getAccessToken()).toBe('oss-lite-token');
  });

  it('getActiveAccount returns null', () => {
    expect(authService.getActiveAccount()).toBeNull();
  });

  it('isReady returns true', () => {
    expect(authService.isReady()).toBe(true);
  });

  it('initialize, signIn, signOut are no-ops', () => {
    expect(() => authService.initialize()).not.toThrow();
    expect(() => authService.signIn()).not.toThrow();
    expect(() => authService.signOut()).not.toThrow();
  });
});
