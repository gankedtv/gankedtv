import { api } from './client'

export interface GameListItem {
  id: number
  name: string
  slug: string
  tag: string
  coverUrl: string | null
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
}
