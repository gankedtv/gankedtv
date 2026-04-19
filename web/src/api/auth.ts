import { api, BASE_URL } from './client'

export interface MeResponse {
  id: string
  username: string
  email: string | null
  bio: string | null
  avatarUrl: string | null
  createdAt: string
}

export interface RefreshResponse {
  token: string
  refresh: string
  expiresIn: number
}

export async function me(): Promise<MeResponse> {
  return api<MeResponse>('/me')
}

export function oauthStartUrl(provider: 'discord' | 'google', returnTo?: string): string {
  let url = `${BASE_URL}/auth/${provider}/start`
  if (returnTo) {
    url += `?returnTo=${encodeURIComponent(returnTo)}`
  }
  return url
}
