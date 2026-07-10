import { api } from './client'
import type { UserSummary } from './follows'

export interface PresenceSummary {
  online: number
  followsOnline: UserSummary[]
}

const CID_KEY = 'presence_cid'

// Stable per-browser id so anonymous viewers count as one person across polls
// (the server falls back to IP keying without it, which collapses behind proxies).
// Returns null when localStorage is unavailable (private mode) — the server then
// keys this caller by IP, which is still a valid heartbeat.
export function getClientId(): string | null {
  try {
    let cid = localStorage.getItem(CID_KEY)
    if (!cid) {
      cid = crypto.randomUUID()
      localStorage.setItem(CID_KEY, cid)
    }
    return cid
  } catch {
    return null
  }
}

export const presence = {
  // The GET doubles as this browser's heartbeat: the server records the caller,
  // then returns the summary.
  summary(): Promise<PresenceSummary> {
    const cid = getClientId()
    const qs = cid ? `?cid=${encodeURIComponent(cid)}` : ''
    return api<PresenceSummary>(`/presence/summary${qs}`)
  },
}
