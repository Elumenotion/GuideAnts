export interface UserDto {
  id: string;
  name: string;
  email: string;
}

export interface UpdateCurrentUserRequest {
  name: string;
  email: string;
}
