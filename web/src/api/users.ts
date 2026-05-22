import { api } from './client'
import type { ClipFeedItem } from './clips'

export interface UserProfile {
  id: string
  username: string
  bio: string | null
  avatarUrl: string | null
  createdAt: string
  followerCount: number
  followingCount: number
  // null when the caller is unauthenticated or viewing their own profile; otherwise the
  // signed-in user's follow state for this profile.
  followedByMe: boolean | null
  clips: ClipFeedItem[]
}

export const users = {
  getByUsername(username: string): Promise<UserProfile> {
    return api<UserProfile>(`/users/${encodeURIComponent(username)}`)
  },
}
