import { api } from './client'

export interface AuthorSummary {
  id: string
  username: string
  avatarUrl: string | null
}

export interface ClipFeedItem {
  id: string
  title: string
  description: string | null
  thumbnailKey: string | null
  durationSecs: number | null
  viewCount: number
  likeCount: number
  createdAt: string
  author: AuthorSummary
  likedByMe: boolean
}

export interface ClipDetail {
  id: string
  title: string
  description: string | null
  videoUrl: string
  videoUrlExpiresAt: string
  thumbnailKey: string | null
  durationSecs: number | null
  width: number | null
  height: number | null
  viewCount: number
  likeCount: number
  createdAt: string
  author: AuthorSummary
  likedByMe: boolean
}

export interface UploadUrl {
  url: string
  expiresAt: string
}

export interface CreateClipBody {
  title: string
  description: string | null
  gameId: number | null
  visibility: 'public' | 'unlisted'
}

export interface CompleteClipResult {
  id: string
  fileSizeBytes: number
}

export interface LikeResult {
  likeCount: number
  liked: boolean
}

export const clips = {
  getDetail(id: string): Promise<ClipDetail> {
    return api<ClipDetail>(`/clips/${encodeURIComponent(id)}`)
  },

  create(body: CreateClipBody): Promise<{ id: string }> {
    return api<{ id: string }>('/clips', { method: 'POST', body })
  },

  getUploadUrl(id: string): Promise<UploadUrl> {
    return api<UploadUrl>(`/clips/${encodeURIComponent(id)}/upload-url`, { method: 'POST' })
  },

  complete(id: string): Promise<CompleteClipResult> {
    return api<CompleteClipResult>(`/clips/${encodeURIComponent(id)}/complete`, { method: 'POST' })
  },

  like(id: string): Promise<LikeResult> {
    return api<LikeResult>(`/clips/${encodeURIComponent(id)}/like`, { method: 'POST' })
  },

  unlike(id: string): Promise<LikeResult> {
    return api<LikeResult>(`/clips/${encodeURIComponent(id)}/like`, { method: 'DELETE' })
  },
}
