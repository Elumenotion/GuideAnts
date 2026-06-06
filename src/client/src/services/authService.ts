import type { AppRole } from '../types/user';

export interface AuthAccount {
  id: string;
  name: string;
  email: string;
  role: AppRole;
  mustChangePassword: boolean;
}

let activeAccount: AuthAccount | null = null;

export const authFetchCredentials: RequestCredentials = 'include';

export function withAuthFetchInit(options: RequestInit = {}): RequestInit {
  return {
    ...options,
    credentials: authFetchCredentials,
  };
}

export function withAuthHeaders(headers?: HeadersInit): Headers {
  return new Headers(headers);
}

function setActiveAccount(account: AuthAccount | null): void {
  activeAccount = account;
}

function clearAuthState(): void {
  activeAccount = null;
}

function getActiveAccount(): AuthAccount | null {
  return activeAccount;
}

const authService = {
  getActiveAccount,
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
