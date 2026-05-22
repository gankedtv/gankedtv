import { api } from './client'
import type { AuthorSummary } from './clips'

export interface CommentItem {
  id: string
  // null when the comment is soft-deleted — render a `[deleted]` placeholder.
  body: string | null
  author: AuthorSummary
  // null for a top-level comment; set to the top-level comment id for a reply.
  parentId: string | null
  createdAt: string
  // Total live replies on a top-level comment (0 for replies themselves).
  replyCount: number
  // First page of inline replies on a top-level comment (oldest-first; empty for replies).
  replies: CommentItem[]
  deleted: boolean
}

export interface CommentListResponse {
  items: CommentItem[]
  nextCursor: string | null
}

export interface CommentListQuery {
  cursor?: string | null
  limit?: number
}

export interface CreateCommentBody {
  body: string
  parentId?: string | null
}

function buildQuery(query: CommentListQuery): string {
  const params = new URLSearchParams()
  if (query.cursor) params.set('cursor', query.cursor)
  if (query.limit !== undefined) params.set('limit', String(query.limit))
  const qs = params.toString()
  return qs ? `?${qs}` : ''
}

export const comments = {
  list(clipId: string, query: CommentListQuery = {}): Promise<CommentListResponse> {
    return api<CommentListResponse>(
      `/clips/${encodeURIComponent(clipId)}/comments${buildQuery(query)}`,
    )
  },

  listReplies(commentId: string, query: CommentListQuery = {}): Promise<CommentListResponse> {
    return api<CommentListResponse>(
      `/comments/${encodeURIComponent(commentId)}/replies${buildQuery(query)}`,
    )
  },

  create(clipId: string, body: CreateCommentBody): Promise<CommentItem> {
    return api<CommentItem>(`/clips/${encodeURIComponent(clipId)}/comments`, {
      method: 'POST',
      body,
    })
  },

  delete(commentId: string): Promise<void> {
    return api<void>(`/comments/${encodeURIComponent(commentId)}`, { method: 'DELETE' })
  },
}
