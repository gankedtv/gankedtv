import { api } from './client'

// Device Authorization Grant (RFC 8628) — browser-side surface only. The desktop client owns
// `POST /auth/device` (start) and `POST /auth/device/token` (poll); the web app just lets a
// signed-in user look up and approve/deny a pending request by its user code.
export interface DeviceLookupResponse {
  clientName: string | null
  status: string
}

export const device = {
  lookup(userCode: string): Promise<DeviceLookupResponse> {
    return api<DeviceLookupResponse>(`/me/device/${encodeURIComponent(userCode)}`)
  },

  approve(userCode: string): Promise<void> {
    return api<void>('/me/device/approve', { method: 'POST', body: { userCode } })
  },

  deny(userCode: string): Promise<void> {
    return api<void>('/me/device/deny', { method: 'POST', body: { userCode } })
  },
}
