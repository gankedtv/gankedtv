import { api } from './client'
import type { ClipFeedItem } from './clips'
import type { GameListItem } from './games'

export type SearchType = 'clips' | 'games' | 'all'

export interface SearchResponse {
  clips: ClipFeedItem[]
  games: GameListItem[]
}

export const search = {
  query(q: string, opts?: { type?: SearchType; limit?: number }): Promise<SearchResponse> {
    const params = new URLSearchParams({ q })
    if (opts?.type) params.set('type', opts.type)
    if (opts?.limit !== undefined) params.set('limit', String(opts.limit))
    return api<SearchResponse>(`/search?${params.toString()}`)
  },
}
