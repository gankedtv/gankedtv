import { api } from './client'

export interface UserSummary {
  id: string
  username: string
  avatarUrl: string | null
}

export interface UserSummaryPage {
  items: UserSummary[]
  nextCursor: string | null
}

export interface UserListQuery {
  cursor?: string | null
  limit?: number
}

function buildList(path: string, username: string, query: UserListQuery): Promise<UserSummaryPage> {
  const params = new URLSearchParams()
  if (query.cursor) params.set('cursor', query.cursor)
  if (query.limit !== undefined) params.set('limit', String(query.limit))
  const qs = params.toString()
  return api<UserSummaryPage>(`/users/${encodeURIComponent(username)}/${path}${qs ? `?${qs}` : ''}`)
}

export const follows = {
  follow(username: string): Promise<void> {
    return api<void>(`/users/${encodeURIComponent(username)}/follow`, { method: 'POST' })
  },

  unfollow(username: string): Promise<void> {
    return api<void>(`/users/${encodeURIComponent(username)}/follow`, { method: 'DELETE' })
  },

  listFollowers(username: string, query: UserListQuery = {}): Promise<UserSummaryPage> {
    return buildList('followers', username, query)
  },

  listFollowing(username: string, query: UserListQuery = {}): Promise<UserSummaryPage> {
    return buildList('following', username, query)
  },
}
