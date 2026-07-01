import { api } from './client'

// Metadata view returned by GET /me/api-keys — never carries the secret. Keys are minted by
// the device-authorization flow (see api/device.ts); this module only views and revokes them.
export interface ApiKeyItem {
  id: string
  name: string | null
  keyPrefix: string
  createdAt: string
  lastUsedAt: string | null
  expiresAt: string | null
  revokedAt: string | null
}

export const apiKeys = {
  list(): Promise<ApiKeyItem[]> {
    return api<ApiKeyItem[]>('/me/api-keys')
  },

  revoke(id: string): Promise<void> {
    return api<void>(`/me/api-keys/${encodeURIComponent(id)}`, { method: 'DELETE' })
  },
}
