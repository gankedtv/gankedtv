import { api } from './client'
import type { AuthorSummary } from './clips'

export type NotificationType = 'like' | 'comment' | 'follow'

// Slim shape — the dropdown only needs enough to label the row and route on click. Full clip
// thumbnails would force presigning into the unread-count hot path, so the server keeps the
// payload narrow on purpose.
export interface NotificationClipSummary {
  id: string
  shareCode: string
  title: string
}

export interface NotificationCommentPreview {
  id: string
  // null when the comment is soft-deleted — mirrors CommentItem.body.
  body: string | null
}

export interface NotificationItem {
  id: string
  type: NotificationType
  actor: AuthorSummary
  // Set on `like` and `comment`; null on `follow`.
  clip: NotificationClipSummary | null
  // Set on `comment` only.
  comment: NotificationCommentPreview | null
  createdAt: string
  // null = unread.
  readAt: string | null
}

export interface NotificationListResponse {
  items: NotificationItem[]
  nextCursor: string | null
}

export interface NotificationListQuery {
  cursor?: string | null
  limit?: number
}

export interface UnreadCountResponse {
  count: number
}

export interface MarkAllReadResponse {
  marked: number
}

function buildQuery(query: NotificationListQuery): string {
  const params = new URLSearchParams()
  if (query.cursor) params.set('cursor', query.cursor)
  if (query.limit !== undefined) params.set('limit', String(query.limit))
  const qs = params.toString()
  return qs ? `?${qs}` : ''
}

export const notifications = {
  list(query: NotificationListQuery = {}): Promise<NotificationListResponse> {
    return api<NotificationListResponse>(`/me/notifications${buildQuery(query)}`)
  },

  unreadCount(): Promise<UnreadCountResponse> {
    return api<UnreadCountResponse>('/me/notifications/unread-count')
  },

  markAllRead(): Promise<MarkAllReadResponse> {
    return api<MarkAllReadResponse>('/me/notifications/read', { method: 'POST' })
  },

  markOneRead(id: string): Promise<void> {
    return api<void>(`/me/notifications/${encodeURIComponent(id)}/read`, { method: 'POST' })
  },
}
