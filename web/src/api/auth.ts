import { api, BASE_URL } from './client'

export interface MeResponse {
  id: string
  username: string
  email: string | null
  bio: string | null
  avatarUrl: string | null
  createdAt: string
  // True when the account has a password set (covers both password-registered and
  // OAuth-then-attached accounts). SettingsPasswordView uses this to switch between
  // "Set password" (first-time) and "Change password" (rotation) copy.
  hasPassword: boolean
}

export interface RefreshResponse {
  token: string
  refresh: string
  expiresIn: number
}

export type TokenResponse = RefreshResponse

export async function me(): Promise<MeResponse> {
  // /auth/me — not bare /me — to avoid tripping tracker blockers (Brave, uBlock,
  // Arc, corporate DLP) that pattern-match analytics endpoints on "/me".
  return api<MeResponse>('/auth/me')
}

export function oauthStartUrl(provider: 'discord' | 'google', returnTo?: string): string {
  let url = `${BASE_URL}/auth/${provider}/start`
  if (returnTo) {
    url += `?returnTo=${encodeURIComponent(returnTo)}`
  }
  return url
}

export interface RegisterPayload {
  email: string
  username: string
  password: string
}

export interface LoginPayload {
  email: string
  password: string
}

export function register(payload: RegisterPayload): Promise<TokenResponse> {
  return api<TokenResponse>('/auth/register', { method: 'POST', body: payload })
}

export function login(payload: LoginPayload): Promise<TokenResponse> {
  return api<TokenResponse>('/auth/login', { method: 'POST', body: payload })
}

// `currentPassword` is required only when the caller already has a password on file.
// OAuth-only users attaching a password for the first time pass null — the server
// trusts the OAuth login that minted the token as proof of account control.
export function setPassword(currentPassword: string | null, newPassword: string): Promise<void> {
  return api<void>('/auth/password', {
    method: 'POST',
    body: { currentPassword, newPassword },
  })
}
