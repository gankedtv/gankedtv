import { api, BASE_URL } from './client'
import { config } from '@/config'

export type UserRole = 'user' | 'moderator' | 'admin'

export type AvatarSource = 'upload' | `oauth:${string}` | null

export interface SocialLinks {
  twitch: string | null
  youtube: string | null
  twitter: string | null
}

export interface MeResponse {
  id: string
  username: string
  email: string | null
  bio: string | null
  avatarUrl: string | null
  // Where the active avatar came from. "upload" means a user-uploaded picture; an
  // "oauth:*" value means the OAuth provider's CDN URL. Drives the "Reset to OAuth avatar"
  // affordance in the edit modal.
  avatarSource: AvatarSource
  // The provider's most recent avatar URL (stashed on every OAuth login). Non-null when
  // the user has logged in via an OAuth provider at least once, regardless of whether they
  // currently use an uploaded avatar.
  oauthAvatarUrl: string | null
  bannerUrl: string | null
  accentColor: string | null
  socialLinks: SocialLinks | null
  createdAt: string
  // True when the account has a password set (covers both password-registered and
  // OAuth-then-attached accounts). SettingsPasswordView uses this to switch between
  // "Set password" (first-time) and "Change password" (rotation) copy.
  hasPassword: boolean
  // Authorization role. The router guard and AppNav gate the admin surface off this.
  role: UserRole
}

// PATCH /auth/me body. Any field omitted leaves the corresponding value untouched; an
// empty string on accentColor (or any social handle) clears that field. AvatarUrl is NOT
// settable here — uploads use /auth/me/avatar/upload-url + complete, and OAuth refresh
// repopulates the provider-sourced URL automatically.
export interface UpdateMePayload {
  username?: string
  bio?: string | null
  accentColor?: string | null
  socialLinks?: Partial<SocialLinks> | null
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

export function updateMe(payload: UpdateMePayload): Promise<MeResponse> {
  return api<MeResponse>('/auth/me', { method: 'PATCH', body: payload })
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
  acceptedTerms: boolean
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

// Revokes the refresh-token family server-side. Body token in localStorage mode; in
// cookie mode the body is empty and credentials: 'include' lets the server read (and
// clear) the HttpOnly cookie.
export function logout(refresh: string | null): Promise<void> {
  return api('/auth/logout', {
    method: 'POST',
    body: refresh ? { refresh } : {},
    ...(config.useSecureCookies ? { credentials: 'include' as const } : {}),
  })
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
