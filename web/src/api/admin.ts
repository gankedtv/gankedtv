import { api } from './client'
import type { ReportTargetType, ReportReason } from './reports'

export type ReportStatus = 'open' | 'resolved' | 'dismissed'

export interface ReportUserRef {
  id: string
  username: string
  avatarUrl: string | null
}

export interface ReportClipTarget {
  id: string
  title: string
  thumbnailKey: string | null
  visibility: string
  status: string
  owner: ReportUserRef
}

export interface ReportCommentTarget {
  id: string
  clipId: string
  body: string | null
  deletedAt: string | null
  author: ReportUserRef
}

export interface ReportUserTarget {
  id: string
  username: string
  avatarUrl: string | null
  bannedAt: string | null
  role: string
}

// Discriminated union — exactly one of clip / comment / user is non-null per row, matched
// to `targetType`. Components switch on `targetType` for rendering; the field is the
// hydrated row for that target kind.
export interface ReportTarget {
  clip: ReportClipTarget | null
  comment: ReportCommentTarget | null
  user: ReportUserTarget | null
}

export interface ReportItem {
  id: string
  targetType: ReportTargetType
  targetId: string
  reason: ReportReason
  note: string | null
  status: ReportStatus
  createdAt: string
  resolvedAt: string | null
  reporter: ReportUserRef
  target: ReportTarget
}

export interface ReportListResponse {
  items: ReportItem[]
  page: number
  pageSize: number
  total: number
}

export function listReports(params: {
  status?: ReportStatus
  page?: number
  pageSize?: number
}): Promise<ReportListResponse> {
  const q = new URLSearchParams()
  if (params.status) q.set('status', params.status)
  if (params.page) q.set('page', String(params.page))
  if (params.pageSize) q.set('pageSize', String(params.pageSize))
  const qs = q.toString()
  return api<ReportListResponse>(`/admin/reports${qs ? `?${qs}` : ''}`)
}

export function resolveReport(id: string, outcome: 'resolved' | 'dismissed'): Promise<unknown> {
  return api<unknown>(`/admin/reports/${id}/resolve`, {
    method: 'POST',
    body: { outcome },
  })
}

export function hideClip(id: string): Promise<unknown> {
  return api<unknown>(`/admin/clips/${id}/hide`, { method: 'POST', body: {} })
}

export function unhideClip(id: string): Promise<unknown> {
  return api<unknown>(`/admin/clips/${id}/unhide`, { method: 'POST', body: {} })
}

// `gameId: null` clears the clip's game tag entirely — useful when no listed game fits.
export function setClipGame(id: string, gameId: number | null): Promise<unknown> {
  return api<unknown>(`/admin/clips/${id}/game`, {
    method: 'POST',
    body: { gameId },
  })
}

export function removeComment(id: string): Promise<unknown> {
  return api<unknown>(`/admin/comments/${id}/remove`, { method: 'POST', body: {} })
}

export function banUser(id: string, reason?: string): Promise<unknown> {
  return api<unknown>(`/admin/users/${id}/ban`, {
    method: 'POST',
    body: { reason: reason ?? null },
  })
}

export function unbanUser(id: string): Promise<unknown> {
  return api<unknown>(`/admin/users/${id}/unban`, { method: 'POST', body: {} })
}
