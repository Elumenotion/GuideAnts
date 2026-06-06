export type AppRole = 'Pending' | 'Reader' | 'Contributor' | 'Admin';

export interface UserDto {
  id: string;
  name: string;
  email: string;
  role?: AppRole;
  mustChangePassword?: boolean;
  lastLoginAt?: string | null;
}

export interface UpdateCurrentUserRequest {
  name: string;
  email: string;
}
