import { api } from './client'

export interface AuthorSummary {
  id: string
  username: string
  avatarUrl: string | null
}

export interface GameSummary {
  id: number
  name: string
  slug: string
  tag: string
}

export interface ClipFeedItem {
  id: string
  title: string
  description: string | null
  // Presigned GET URL for the thumbnail JPEG. Always set on ready clips (the only
  // status the feed surfaces).
  thumbnailUrl: string
  durationSecs: number | null
  viewCount: number
  likeCount: number
  createdAt: string
  author: AuthorSummary
  game: GameSummary | null
  likedByMe: boolean
  shareCode: string
}

export interface ClipDetail {
  id: string
  title: string
  description: string | null
  videoUrl: string
  videoUrlExpiresAt: string
  thumbnailUrl: string
  durationSecs: number | null
  width: number | null
  height: number | null
  viewCount: number
  likeCount: number
  createdAt: string
  author: AuthorSummary
  game: GameSummary | null
  likedByMe: boolean
  visibility: 'public' | 'unlisted'
  shareCode: string
}

export interface UpdateClipBody {
  title?: string
  description?: string
  gameId?: number | null
  visibility?: 'public' | 'unlisted'
}

export interface ClipFeedPage {
  items: ClipFeedItem[]
  nextCursor: string | null
}

export interface ClipFeedQuery {
  cursor?: string | null
  limit?: number
}

export interface UploadUrl {
  url: string
  expiresAt: string
  // The MIME the server signed the presigned PUT for. The client MUST send this exact
  // value as the request Content-Type — S3/MinIO includes it in the signature and will
  // 403 the upload otherwise.
  contentType: string
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
  feed(query: ClipFeedQuery = {}): Promise<ClipFeedPage> {
    const params = new URLSearchParams()
    if (query.cursor) params.set('cursor', query.cursor)
    if (query.limit !== undefined) params.set('limit', String(query.limit))
    const qs = params.toString()
    return api<ClipFeedPage>(`/clips/feed${qs ? `?${qs}` : ''}`)
  },

  getDetail(id: string): Promise<ClipDetail> {
    return api<ClipDetail>(`/clips/${encodeURIComponent(id)}`)
  },

  getByShareCode(code: string): Promise<ClipDetail> {
    return api<ClipDetail>(`/c/${encodeURIComponent(code)}`)
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

  update(id: string, body: UpdateClipBody): Promise<ClipDetail> {
    return api<ClipDetail>(`/clips/${encodeURIComponent(id)}`, { method: 'PATCH', body })
  },

  delete(id: string): Promise<void> {
    return api<void>(`/clips/${encodeURIComponent(id)}`, { method: 'DELETE' })
  },

  like(id: string): Promise<LikeResult> {
    return api<LikeResult>(`/clips/${encodeURIComponent(id)}/like`, { method: 'POST' })
  },

  unlike(id: string): Promise<LikeResult> {
    return api<LikeResult>(`/clips/${encodeURIComponent(id)}/like`, { method: 'DELETE' })
  },
}
