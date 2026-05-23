import { api } from './client'
import type { ClipFeedPage, ClipFeedQuery } from './clips'

export interface TagSummary {
  id: number
  slug: string
  name: string
  // 0 when nested under a clip; populated by GET /tags (autocomplete).
  clipCount: number
}

export interface TagDetail {
  id: number
  slug: string
  name: string
  clipCount: number
}

export const tags = {
  // Autocomplete dropdown source. Prefix is sent as-is — the server normalizes
  // (lowercases, strips invalid chars) before LIKE-matching, so the client can pass
  // the user's literal keystrokes without local preprocessing.
  autocomplete(prefix: string, limit?: number): Promise<TagSummary[]> {
    const params = new URLSearchParams()
    if (prefix) params.set('prefix', prefix)
    if (limit !== undefined) params.set('limit', String(limit))
    const qs = params.toString()
    return api<TagSummary[]>(`/tags${qs ? `?${qs}` : ''}`)
  },

  getBySlug(slug: string): Promise<TagDetail> {
    return api<TagDetail>(`/tags/${encodeURIComponent(slug)}`)
  },

  clips(slug: string, query: ClipFeedQuery = {}): Promise<ClipFeedPage> {
    const params = new URLSearchParams()
    if (query.cursor) params.set('cursor', query.cursor)
    if (query.limit !== undefined) params.set('limit', String(query.limit))
    const qs = params.toString()
    return api<ClipFeedPage>(`/tags/${encodeURIComponent(slug)}/clips${qs ? `?${qs}` : ''}`)
  },
}
