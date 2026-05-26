import { api } from './client'
import type { AvatarSource } from './auth'

export type ProfileMediaContentType = 'image/png' | 'image/jpeg' | 'image/webp'

export interface ProfileMediaUploadUrl {
  url: string
  expiresAt: string
  // Echo of the MIME the server signed for. Send EXACTLY this string as the upload's
  // Content-Type header — S3 includes it in the signature and 403s on mismatch.
  contentType: string
  // Server-built object key (namespaced by user id + kind). The client must echo it back
  // verbatim to the /complete endpoint so the server can verify ownership without trusting
  // the client to construct keys.
  objectKey: string
}

export interface ProfileMediaCompleted {
  url: string
  objectKey: string
  // Only present on the avatar variant — banner has no "source" concept.
  avatarSource?: AvatarSource
}

export interface ProfileMediaDeleted {
  // The URL after deletion. For an avatar with a stashed OAuth picture this is the restored
  // provider avatar; otherwise null.
  url: string | null
  // Only present on the avatar variant — banner has no "source" concept.
  avatarSource?: AvatarSource
}

function uploadUrl(kind: 'avatar' | 'banner', contentType: ProfileMediaContentType) {
  return api<ProfileMediaUploadUrl>(`/auth/me/${kind}/upload-url`, {
    method: 'POST',
    body: { contentType },
  })
}

function complete(kind: 'avatar' | 'banner', objectKey: string) {
  return api<ProfileMediaCompleted>(`/auth/me/${kind}/complete`, {
    method: 'POST',
    body: { objectKey },
  })
}

function remove(kind: 'avatar' | 'banner') {
  return api<ProfileMediaDeleted>(`/auth/me/${kind}`, { method: 'DELETE' })
}

export const profileMedia = {
  getAvatarUploadUrl: (contentType: ProfileMediaContentType) => uploadUrl('avatar', contentType),
  completeAvatar: (objectKey: string) => complete('avatar', objectKey),
  deleteAvatar: () => remove('avatar'),
  getBannerUploadUrl: (contentType: ProfileMediaContentType) => uploadUrl('banner', contentType),
  completeBanner: (objectKey: string) => complete('banner', objectKey),
  deleteBanner: () => remove('banner'),
}
