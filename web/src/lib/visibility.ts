import type { ClipVisibility } from '@/api/clips'

export interface VisibilityOption {
  value: ClipVisibility
  label: string
  description: string
}

// Shared by the upload wizard and the clip edit dialog so the selector cards never drift.
export const VISIBILITY_OPTIONS: readonly VisibilityOption[] = [
  { value: 'public', label: 'Public', description: 'Visible on feed + search' },
  { value: 'unlisted', label: 'Unlisted', description: 'Only accessible via link' },
  { value: 'private', label: 'Private', description: 'Only visible to you' },
]
