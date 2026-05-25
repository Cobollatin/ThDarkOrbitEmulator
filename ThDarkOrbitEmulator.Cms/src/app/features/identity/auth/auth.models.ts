export interface LoginRequest {
  username: string;
  password: string;
}

export interface LoginResponse {
  authenticated: boolean;
  token?: string;
}

export interface RegisterRequest {
  username: string;
  password: string;
  email?: string | null;
}

export interface RegisterResponse {
  created: boolean;
}

export interface UserProfile {
  username: string;
  email?: string;
  gameUserId: number;
}
