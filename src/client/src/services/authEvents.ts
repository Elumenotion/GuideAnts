import { authService } from './authService';

export const AUTH_EXPIRED_EVENT = 'auth-expired';

export interface AuthExpiredDetail {
  reason?: string;
}

export function broadcastAuthExpired(reason?: string): void {
  // Anonymous startup probes (e.g. GET /auth/me) legitimately return 401.
  // Only surface session expiry after the client has established an account.
  if (!authService.getActiveAccount()) {
    return;
  }

  try {
    const event = new CustomEvent<AuthExpiredDetail>(AUTH_EXPIRED_EVENT, {
      detail: { reason },
    });
    window.dispatchEvent(event);
  } catch {
    const event = new Event(AUTH_EXPIRED_EVENT);
    (event as Event & { detail?: AuthExpiredDetail }).detail = { reason };
    window.dispatchEvent(event);
  }
}
