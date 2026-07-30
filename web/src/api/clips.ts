import { api } from './client'
import type { TagSummary } from './tags'

export interface AuthorSummary {
  id: string
  username: string
  avatarUrl: string | null
}

// User-settable visibility levels. 'public' = in feeds + search; 'unlisted' = hidden from
// feeds but anyone with the link can watch; 'private' = owner-only, the server 404s every
// other viewer. The moderation-only 'hidden' status never round-trips through this type.
export type ClipVisibility = 'public' | 'unlisted' | 'private'

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
  visibility: ClipVisibility
  shareCode: string
  // Set when the clip was ingested via POST /clips/import (Medal.tv / YouTube). Null for
  // direct uploads. The detail page renders a "From {host}" attribution badge linking back
  // to the original.
  importSourceUrl: string | null
  // How the clip entered the platform. 'api' means the create call was authenticated with
  // the author's own device-approved API key (rewynd) — the detail page renders that as a
  // "rewynd verified upload" badge. Server-derived from the auth scheme; never client-set.
  uploadSource: 'web' | 'api' | 'import'
}

export interface UpdateClipBody {
  title?: string
  description?: string
  gameId?: number | null
  visibility?: ClipVisibility
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
  source?: 'public' | 'following' | 'for-you'
  // Filter the feed to a single game (the Home game pills). Composes with source/sort.
  gameId?: number
}

// Windows the likes-ranked `top` sort accepts. Wider than trending's (adds 30d + all-time):
// "top of the day/week/month/all time" are the meaningful ranking windows.
export type TopWindow = '24h' | '7d' | '30d' | 'all'

// Discriminated union: trending + top each REQUIRE a window (server 400s without one) and
// `latest` is the default omitted shape, so `window` is meaningless there. Encoding this in
// the type stops callers from constructing combos the server will reject. `top` keyset-paginates
// like `latest` (real nextCursor); `trending` is a single ranked page (nextCursor always null).
export type ClipFeedQuery =
  | (ClipFeedQueryBase & { sort?: 'latest'; window?: never })
  | (ClipFeedQueryBase & { sort: 'trending'; window: '24h' | '7d' })
  | (ClipFeedQueryBase & { sort: 'top'; window: TopWindow })

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
  visibility: ClipVisibility
  // Optional. When omitted, the clip has no tags. Server normalizes (lowercase,
  // hyphenate, max 5) and get-or-creates rows by slug.
  tags?: string[]
}

// POST /clips/import body. Url is required; everything else is optional — when title
// is empty, the server fills it from the source's metadata (yt-dlp --print-json).
export interface ImportClipBody {
  url: string
  title?: string | null
  description?: string | null
  gameId?: number | null
  visibility?: ClipVisibility
  tags?: string[]
}

export interface ImportClipResult {
  id: string
  // 'importing' on submit; the client polls clips.getDetail until status flips to 'ready'
  // (success) or 'failed' (terminal). Intermediate states ('processing', 'transcoding')
  // come from the existing pipeline and are surfaced for progress UI.
  status: string
}

// Returned by POST /clips/import/preview — metadata-only probe before any clip row exists.
// Lets the wizard show duration + title in step 1 and gate "Continue" when the source
// already exceeds maxClipDurationSecs. Fields are nullable because not every extractor
// reports them; the front-end only gates when durationSecs is known.
export interface ImportClipPreview {
  title: string | null
  durationSecs: number | null
  width: number | null
  height: number | null
  // Best thumbnail URL the platform resolves (Medal CDN, img.youtube.com). Used by the
  // upload wizard to render a real preview frame for any supported source. Always nullable
  // because not every extractor exposes one — the YouTube-derived client-side fallback
  // still works when this is null.
  thumbnailUrl: string | null
  maxClipDurationSecs: number
}

export interface CompleteClipResult {
  id: string
  fileSizeBytes: number
}

// Optional trim range (seconds into the uploaded file) picked in the upload wizard's
// trimmer. Sent with complete(); the server cuts the clip during compression.
export interface ClipTrimRange {
  trimStartSeconds: number
  trimEndSeconds: number
}

export interface LikeResult {
  likeCount: number
  liked: boolean
}

export interface StreamStatus {
  hlsUrl: string | null
  status: 'ready' | 'pending'
}

// Owner-only status probe — returned by GET /clips/{id}/status while the clip is still
// moving through the pipeline (importing → processing → transcoding → ready/failed).
// On failure, failureReason is a short machine-readable code (e.g. 'source_too_long')
// and durationSecs / maxClipDurationSecs let the UI render specific copy.
export interface ClipStatus {
  id: string
  status: string
  shareCode: string
  failureReason: string | null
  durationSecs: number | null
  maxClipDurationSecs: number | null
}

export const clips = {
  feed(query: ClipFeedQuery = {}): Promise<ClipFeedPage> {
    const params = new URLSearchParams()
    if (query.cursor) params.set('cursor', query.cursor)
    if (query.limit !== undefined) params.set('limit', String(query.limit))
    if (query.source) params.set('source', query.source)
    if (query.gameId !== undefined) params.set('gameId', String(query.gameId))
    // Only serialize sort/window for the windowed sorts (trending/top) — `sort=latest`
    // is the default and its variant has no window. The type union enforces this shape
    // statically; the runtime check just mirrors it for the emitted JS.
    if (query.sort === 'trending' || query.sort === 'top') {
      params.set('sort', query.sort)
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

  // Owner-only lightweight status probe. Used by the upload + import wizards to poll for
  // pipeline transitions before the clip is feed-visible (getDetail 404s in non-ready states).
  getStatus(id: string): Promise<ClipStatus> {
    return api<ClipStatus>(`/clips/${encodeURIComponent(id)}/status`)
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

  // POST /clips/import — paste a Medal.tv / YouTube URL and the server fetches the source
  // via yt-dlp, then feeds it into the same thumbnail → compress → ready pipeline as a
  // direct upload. Returns immediately; caller polls clips.getDetail(id) for status.
  importFromUrl(body: ImportClipBody): Promise<ImportClipResult> {
    return api<ImportClipResult>('/clips/import', { method: 'POST', body })
  },

  // POST /clips/import/preview — metadata-only probe (no download, no clip row). Used by
  // step 1 of the wizard to surface duration + title and gate "Continue" when the source
  // is already too long, sparing the user from filling out step 2 for a doomed clip.
  previewImport(url: string): Promise<ImportClipPreview> {
    return api<ImportClipPreview>('/clips/import/preview', { method: 'POST', body: { url } })
  },

  getUploadUrl(id: string): Promise<UploadUrl> {
    return api<UploadUrl>(`/clips/${encodeURIComponent(id)}/upload-url`, { method: 'POST' })
  },

  complete(id: string, trim?: ClipTrimRange): Promise<CompleteClipResult> {
    return api<CompleteClipResult>(`/clips/${encodeURIComponent(id)}/complete`, {
      method: 'POST',
      ...(trim ? { body: trim } : {}),
    })
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
