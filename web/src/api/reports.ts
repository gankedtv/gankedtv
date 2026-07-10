import { api } from './client'

export type ReportTargetType = 'clip' | 'comment' | 'user'

export type ReportReason =
  'spam' | 'harassment' | 'hate' | 'nsfw' | 'violence' | 'wrong_game' | 'other'

const PATHS: Record<ReportTargetType, (id: string) => string> = {
  clip: (id) => `/clips/${id}/report`,
  comment: (id) => `/comments/${id}/report`,
  user: (id) => `/users/${id}/report`,
}

export interface CreateReportResponse {
  id: string
}

// Server-side validation: `other` requires a non-empty note. Mirrored client-side in the
// ReportDialog so we surface the constraint before submission, but the server is still the
// authoritative gate.
export function report(
  targetType: ReportTargetType,
  targetId: string,
  reason: ReportReason,
  note?: string,
): Promise<CreateReportResponse> {
  return api<CreateReportResponse>(PATHS[targetType](targetId), {
    method: 'POST',
    body: { reason, note: note ?? null },
  })
}
