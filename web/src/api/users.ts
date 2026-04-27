import { api } from './client'
import type { ClipFeedItem } from './clips'

export interface UserProfile {
  id: string
  username: string
  bio: string | null
  avatarUrl: string | null
  createdAt: string
  clips: ClipFeedItem[]
}

export const users = {
  getByUsername(username: string): Promise<UserProfile> {
    return api<UserProfile>(`/users/${encodeURIComponent(username)}`)
  },
}
