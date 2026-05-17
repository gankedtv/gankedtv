import { api } from './client'
import type { ClipFeedPage, ClipFeedQuery } from './clips'

export interface GameListItem {
  id: number
  name: string
  slug: string
  tag: string
  coverUrl: string | null
}

export interface GameDetail {
  id: number
  name: string
  slug: string
  tag: string
  coverUrl: string | null
  clipCount: number
}

export const games = {
  list(limit?: number): Promise<GameListItem[]> {
    const qs = limit !== undefined ? `?limit=${limit}` : ''
    return api<GameListItem[]>(`/games${qs}`)
  },

  search(query: string, limit?: number): Promise<GameListItem[]> {
    const params = new URLSearchParams({ search: query })
    if (limit !== undefined) params.set('limit', String(limit))
    return api<GameListItem[]>(`/games?${params.toString()}`)
  },

  getBySlug(slug: string): Promise<GameDetail> {
    return api<GameDetail>(`/games/${encodeURIComponent(slug)}`)
  },

  clips(slug: string, query: ClipFeedQuery = {}): Promise<ClipFeedPage> {
    const params = new URLSearchParams()
    if (query.cursor) params.set('cursor', query.cursor)
    if (query.limit !== undefined) params.set('limit', String(query.limit))
    const qs = params.toString()
    return api<ClipFeedPage>(`/games/${encodeURIComponent(slug)}/clips${qs ? `?${qs}` : ''}`)
  },
}
