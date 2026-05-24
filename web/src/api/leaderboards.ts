import { api } from './client'
import type { ClipFeedItem, GameSummary } from './clips'

export type LeaderboardWindow = 'week' | 'month' | 'all'

export interface LeaderboardEntry {
  rank: number
  windowLikes: number
  clip: ClipFeedItem
}

export interface TopGameEntry {
  rank: number
  windowLikes: number
  clipCount: number
  game: GameSummary
  coverUrl: string | null
}

export interface GameLeaderboardResponse {
  window: LeaderboardWindow
  game: GameSummary
  entries: LeaderboardEntry[]
}

export interface GlobalLeaderboardResponse {
  window: LeaderboardWindow
  topClips: LeaderboardEntry[]
  topGames: TopGameEntry[]
}

export interface LeaderboardQuery {
  window: LeaderboardWindow
  limit?: number
}

export interface GlobalLeaderboardQuery {
  window: LeaderboardWindow
  clipsLimit?: number
  gamesLimit?: number
}

export const leaderboards = {
  global(query: GlobalLeaderboardQuery): Promise<GlobalLeaderboardResponse> {
    const params = new URLSearchParams()
    params.set('window', query.window)
    if (query.clipsLimit !== undefined) params.set('clipsLimit', String(query.clipsLimit))
    if (query.gamesLimit !== undefined) params.set('gamesLimit', String(query.gamesLimit))
    return api<GlobalLeaderboardResponse>(`/leaderboards?${params.toString()}`)
  },

  forGame(slug: string, query: LeaderboardQuery): Promise<GameLeaderboardResponse> {
    const params = new URLSearchParams()
    params.set('window', query.window)
    if (query.limit !== undefined) params.set('limit', String(query.limit))
    return api<GameLeaderboardResponse>(
      `/games/${encodeURIComponent(slug)}/leaderboard?${params.toString()}`,
    )
  },
}
