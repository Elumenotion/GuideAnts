import type { AppRole } from '../types/user';

const AUTH_TOKEN_KEY = 'guideants.auth.token';
const AUTH_ACCOUNT_KEY = 'guideants.auth.account';

export interface AuthAccount {
  id: string;
  name: string;
  email: string;
  role: AppRole;
  mustChangePassword: boolean;
}

let accessToken: string | null = null;
let activeAccount: AuthAccount | null = null;

function setAccessToken(token: string | null): void {
  accessToken = token && token.trim().length > 0 ? token : null;
}

function setActiveAccount(account: AuthAccount | null): void {
  activeAccount = account;
}

function clearAuthState(): void {
  setAccessToken(null);
  setActiveAccount(null);
  if (typeof window === 'undefined') {
    return;
  }
  try {
    window.sessionStorage.removeItem(AUTH_TOKEN_KEY);
    window.sessionStorage.removeItem(AUTH_ACCOUNT_KEY);
  } catch {
    // Storage cleanup is best-effort only.
  }
}

function getAccessToken(): string | null {
  return accessToken;
}

function getActiveAccount(): AuthAccount | null {
  return activeAccount;
}

export function withAuthHeaders(headers?: HeadersInit): Headers {
  const merged = new Headers(headers);
  const token = getAccessToken();
  if (token && !merged.has('Authorization')) {
    merged.set('Authorization', `Bearer ${token}`);
  }
  return merged;
}

const authService = {
  getAccessToken,
  getActiveAccount,
  setAccessToken,
  setActiveAccount,
  clearAuthState,
  initialize(): void {},
  signIn(): void {},
  signOut(): void {
    clearAuthState();
  },
  isReady(): boolean {
    return true;
  },
};

export { authService };
