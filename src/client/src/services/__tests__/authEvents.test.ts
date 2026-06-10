import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AUTH_EXPIRED_EVENT } from '../authEvents';

const mockGetActiveAccount = vi.fn();

vi.mock('../authService', () => ({
  authService: {
    getActiveAccount: () => mockGetActiveAccount(),
  },
}));

import { broadcastAuthExpired } from '../authEvents';

describe('authEvents', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('does not dispatch when there is no active account', () => {
    mockGetActiveAccount.mockReturnValue(null);
    const listener = vi.fn();
    window.addEventListener(AUTH_EXPIRED_EVENT, listener);

    broadcastAuthExpired('Session ended');

    expect(listener).not.toHaveBeenCalled();
    window.removeEventListener(AUTH_EXPIRED_EVENT, listener);
  });

  it('dispatches auth-expired event when active account exists', () => {
    mockGetActiveAccount.mockReturnValue({
      id: 'u1',
      name: 'User',
      email: 'user@example.com',
      role: 'Contributor',
      mustChangePassword: false,
    });

    const listener = vi.fn();
    window.addEventListener(AUTH_EXPIRED_EVENT, listener);

    broadcastAuthExpired('Token expired');

    expect(listener).toHaveBeenCalledTimes(1);
    const event = listener.mock.calls[0][0] as CustomEvent<{ reason?: string }>;
    expect(event.detail).toEqual({ reason: 'Token expired' });
    window.removeEventListener(AUTH_EXPIRED_EVENT, listener);
  });

  it('falls back to plain Event when CustomEvent construction fails', () => {
    mockGetActiveAccount.mockReturnValue({
      id: 'u1',
      name: 'User',
      email: 'user@example.com',
      role: 'Contributor',
      mustChangePassword: false,
    });

    const originalCustomEvent = global.CustomEvent;
    // @ts-expect-error test override
    global.CustomEvent = function BrokenCustomEvent() {
      throw new Error('CustomEvent unavailable');
    };

    const listener = vi.fn();
    window.addEventListener(AUTH_EXPIRED_EVENT, listener);

    broadcastAuthExpired('fallback reason');

    expect(listener).toHaveBeenCalledTimes(1);
    const event = listener.mock.calls[0][0] as Event & { detail?: { reason?: string } };
    expect(event.type).toBe(AUTH_EXPIRED_EVENT);
    expect(event.detail).toEqual({ reason: 'fallback reason' });

    global.CustomEvent = originalCustomEvent;
    window.removeEventListener(AUTH_EXPIRED_EVENT, listener);
  });
});
