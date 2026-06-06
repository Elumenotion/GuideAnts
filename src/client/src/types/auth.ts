import type { AppRole } from './user';

export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  name: string;
  email: string;
  password: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface AuthResponse {
  userId: string;
  name: string;
  email: string;
  role: AppRole;
  mustChangePassword: boolean;
}

export interface AuthMeResponse {
  userId: string;
  name: string;
  email: string;
  role: AppRole;
  mustChangePassword: boolean;
  lastLoginAt?: string | null;
}
