export const AUTH_EXPIRED_EVENT = 'auth-expired';

export interface AuthExpiredDetail {
  reason?: string;
}

export function broadcastAuthExpired(reason?: string): void {
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
