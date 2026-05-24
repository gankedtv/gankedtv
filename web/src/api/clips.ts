import { api } from './client'
import type { TagSummary } from './tags'

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
  tags: TagSummary[]
  likedByMe: boolean
  shareCode: string
}

export interface ClipDetail {
  id: string
  title: string
  description: string | null
  videoUrl: string
  videoUrlExpiresAt: string
  // Codec of the stored master ("av1" / "h264" / null). The player uses it to decide whether
  // to play videoUrl directly or request a just-in-time H.264 stream.
  videoCodec: string | null
  thumbnailUrl: string
  durationSecs: number | null
  width: number | null
  height: number | null
  viewCount: number
  likeCount: number
  createdAt: string
  author: AuthorSummary
  game: GameSummary | null
  tags: TagSummary[]
  likedByMe: boolean
  visibility: 'public' | 'unlisted'
  shareCode: string
}

export interface UpdateClipBody {
  title?: string
  description?: string
  gameId?: number | null
  visibility?: 'public' | 'unlisted'
  // Omitted = leave tags unchanged. Empty array = clear all tags. Otherwise =
  // replace the tag set with this exact list (post-normalization server-side).
  tags?: string[]
}

export interface ClipFeedPage {
  items: ClipFeedItem[]
  nextCursor: string | null
}

interface ClipFeedQueryBase {
  cursor?: string | null
  limit?: number
  source?: 'public' | 'following'
}

// Discriminated union: trending REQUIRES a window (server 400s without one) and
// `latest` is the default omitted shape, so `window` is meaningless there. Encoding
// this in the type stops callers from constructing combos the server will reject.
export type ClipFeedQuery =
  | (ClipFeedQueryBase & { sort?: 'latest'; window?: never })
  | (ClipFeedQueryBase & { sort: 'trending'; window: '24h' | '7d' })

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
  // Optional. When omitted, the clip has no tags. Server normalizes (lowercase,
  // hyphenate, max 5) and get-or-creates rows by slug.
  tags?: string[]
}

export interface CompleteClipResult {
  id: string
  fileSizeBytes: number
}

export interface LikeResult {
  likeCount: number
  liked: boolean
}

export interface StreamStatus {
  hlsUrl: string | null
  status: 'ready' | 'pending'
}

export const clips = {
  feed(query: ClipFeedQuery = {}): Promise<ClipFeedPage> {
    const params = new URLSearchParams()
    if (query.cursor) params.set('cursor', query.cursor)
    if (query.limit !== undefined) params.set('limit', String(query.limit))
    if (query.source) params.set('source', query.source)
    // Only serialize sort/window when trending — `sort=latest` is the default
    // and the latest variant has no window. The type union enforces this shape
    // statically; the runtime check just mirrors it for the emitted JS.
    if (query.sort === 'trending') {
      params.set('sort', 'trending')
      params.set('window', query.window)
    }
    const qs = params.toString()
    return api<ClipFeedPage>(`/clips/feed${qs ? `?${qs}` : ''}`)
  },

  // GET /clips/featured — daily "Clip of the Day" pick. Returns null on 204
  // (no eligible clip today). HomeView falls back to the newest clip from
  // /clips/feed when this is null so the hero never goes blank.
  featured(): Promise<ClipFeedItem | null> {
    // The api() client returns undefined for 204 (see client.ts); normalize to
    // null so callers can use the explicit `null` sentinel.
    return api<ClipFeedItem | undefined>('/clips/featured').then((r) => r ?? null)
  },

  // POST /clips/{id}/view — anonymous-friendly view ping. Server returns 204 on success,
  // dedup hit, and not-found (silent no-op). Fire-and-forget from the player after ~3s
  // of playback; failures don't bubble.
  recordView(id: string): Promise<void> {
    return api<void>(`/clips/${encodeURIComponent(id)}/view`, { method: 'POST' })
  },

  getDetail(id: string): Promise<ClipDetail> {
    return api<ClipDetail>(`/clips/${encodeURIComponent(id)}`)
  },

  getByShareCode(code: string): Promise<ClipDetail> {
    return api<ClipDetail>(`/c/${encodeURIComponent(code)}`)
  },

  // Requests the just-in-time H.264 HLS stream for a clip whose stored master the device
  // can't decode. status: 'ready' (hlsUrl set) or 'pending' (a rendition is building — poll).
  getStream(id: string): Promise<StreamStatus> {
    return api<StreamStatus>(`/clips/${encodeURIComponent(id)}/stream`)
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
