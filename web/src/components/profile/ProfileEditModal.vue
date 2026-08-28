<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { ApiError } from '@/api/client'
import { updateMe, type MeResponse, type UpdateMePayload } from '@/api/auth'
import { profileMedia, type ProfileMediaContentType } from '@/api/profileMedia'
import { putToPresignedUrl } from '@/lib/upload'
import { useAuthStore } from '@/stores/auth'
import { safeImageUrl } from '@/lib/url'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: []; saved: []; error: [message: string] }>()

const auth = useAuthStore()

const AVATAR_MAX = 512
const BANNER_W = 1600
const BANNER_H = 400

// Preset accent swatches — mint-first plus a few neutrals. They're a convenience;
// the picker still allows any hex.
const ACCENT_PRESETS = ['#00E5A0', '#7DFFD8', '#00A376', '#F0F0F4', '#888070']

const username = ref('')
const bio = ref('')
const accent = ref<string>('')
const twitch = ref('')
const youtube = ref('')
const twitter = ref('')

const avatarFile = ref<Blob | null>(null)
const avatarPreview = ref<string | null>(null)
const avatarMime = ref<ProfileMediaContentType | null>(null)
const bannerFile = ref<Blob | null>(null)
const bannerPreview = ref<string | null>(null)
const bannerMime = ref<ProfileMediaContentType | null>(null)

const submitting = ref(false)
const errorMsg = ref<string | null>(null)
const resetBusy = ref(false)

// Recompute defaults when the modal re-opens so an open→close→reopen cycle picks up the
// latest server state (the parent has called fetchMe() since the last open).
watch(
  () => props.open,
  (open) => {
    if (!open) return
    const me = auth.user
    if (!me) return
    username.value = me.username
    bio.value = me.bio ?? ''
    accent.value = me.accentColor ?? ''
    twitch.value = me.socialLinks?.twitch ?? ''
    youtube.value = me.socialLinks?.youtube ?? ''
    twitter.value = me.socialLinks?.twitter ?? ''
    clearAvatarChoice()
    clearBannerChoice()
    errorMsg.value = null
  },
  { immediate: true },
)

function clearAvatarChoice() {
  if (avatarPreview.value) URL.revokeObjectURL(avatarPreview.value)
  avatarFile.value = null
  avatarPreview.value = null
  avatarMime.value = null
}
function clearBannerChoice() {
  if (bannerPreview.value) URL.revokeObjectURL(bannerPreview.value)
  bannerFile.value = null
  bannerPreview.value = null
  bannerMime.value = null
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open && !submitting.value) emit('close')
}
onMounted(() => window.addEventListener('keydown', onKey))
onUnmounted(() => {
  window.removeEventListener('keydown', onKey)
  clearAvatarChoice()
  clearBannerChoice()
})

// Centre-crop to a square then downscale to <=AVATAR_MAX. Encoding format follows the
// source MIME when supported (PNG/JPEG/WebP); falls back to JPEG for everything else.
async function resizeToSquare(
  file: File,
  max: number,
): Promise<{ blob: Blob; mime: ProfileMediaContentType }> {
  const img = await readImage(file)
  const side = Math.min(img.width, img.height)
  const sx = (img.width - side) / 2
  const sy = (img.height - side) / 2
  const target = Math.min(side, max)
  const canvas = document.createElement('canvas')
  canvas.width = target
  canvas.height = target
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('Canvas not supported')
  ctx.drawImage(img, sx, sy, side, side, 0, 0, target, target)
  const mime = chooseEncoderMime(file.type)
  const blob = await canvasToBlob(canvas, mime)
  return { blob, mime }
}

async function resizeToCover(
  file: File,
  targetW: number,
  targetH: number,
): Promise<{ blob: Blob; mime: ProfileMediaContentType }> {
  const img = await readImage(file)
  const aspectTarget = targetW / targetH
  const aspectSrc = img.width / img.height
  let sx = 0
  let sy = 0
  let sw = img.width
  let sh = img.height
  if (aspectSrc > aspectTarget) {
    // Source is wider than target — crop horizontal sides.
    sw = img.height * aspectTarget
    sx = (img.width - sw) / 2
  } else {
    sh = img.width / aspectTarget
    sy = (img.height - sh) / 2
  }
  const canvas = document.createElement('canvas')
  canvas.width = targetW
  canvas.height = targetH
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('Canvas not supported')
  ctx.drawImage(img, sx, sy, sw, sh, 0, 0, targetW, targetH)
  const mime = chooseEncoderMime(file.type)
  const blob = await canvasToBlob(canvas, mime)
  return { blob, mime }
}

function chooseEncoderMime(srcMime: string): ProfileMediaContentType {
  if (srcMime === 'image/png' || srcMime === 'image/webp') return srcMime
  // JPEG covers JPEG inputs and any other (HEIC, GIF still-frame) we accidentally let through.
  return 'image/jpeg'
}

function readImage(file: File): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image()
    const url = URL.createObjectURL(file)
    img.onload = () => {
      URL.revokeObjectURL(url)
      resolve(img)
    }
    img.onerror = () => {
      URL.revokeObjectURL(url)
      reject(new Error('Could not decode image'))
    }
    img.src = url
  })
}

function canvasToBlob(canvas: HTMLCanvasElement, mime: ProfileMediaContentType): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (b) => (b ? resolve(b) : reject(new Error('Canvas encode failed'))),
      mime,
      mime === 'image/jpeg' || mime === 'image/webp' ? 0.92 : undefined,
    )
  })
}

async function pickAvatar(e: Event) {
  const file = (e.target as HTMLInputElement).files?.[0]
  if (!file) return
  errorMsg.value = null
  try {
    const { blob, mime } = await resizeToSquare(file, AVATAR_MAX)
    clearAvatarChoice()
    avatarFile.value = blob
    avatarMime.value = mime
    avatarPreview.value = URL.createObjectURL(blob)
  } catch (err) {
    errorMsg.value = (err as Error).message
  } finally {
    ;(e.target as HTMLInputElement).value = ''
  }
}

async function pickBanner(e: Event) {
  const file = (e.target as HTMLInputElement).files?.[0]
  if (!file) return
  errorMsg.value = null
  try {
    const { blob, mime } = await resizeToCover(file, BANNER_W, BANNER_H)
    clearBannerChoice()
    bannerFile.value = blob
    bannerMime.value = mime
    bannerPreview.value = URL.createObjectURL(blob)
  } catch (err) {
    errorMsg.value = (err as Error).message
  } finally {
    ;(e.target as HTMLInputElement).value = ''
  }
}

const hasUploadInFlight = computed(() => submitting.value)

const canShowResetAvatar = computed(
  () => !!auth.user && auth.user.avatarSource === 'upload' && !!auth.user.oauthAvatarUrl,
)

async function resetToOAuthAvatar() {
  if (resetBusy.value) return
  resetBusy.value = true
  errorMsg.value = null
  try {
    await profileMedia.deleteAvatar()
    await auth.fetchMe()
    // Drop any in-modal pick so the UI reflects the restored OAuth picture and the next
    // Save doesn't immediately re-upload what we just reset.
    clearAvatarChoice()
  } catch (err) {
    errorMsg.value = friendlyError(err)
  } finally {
    resetBusy.value = false
  }
}

function friendlyError(err: unknown): string {
  if (err instanceof ApiError) {
    const code = (err.body as { code?: string } | null)?.code
    if (code === 'unsupported_content_type')
      return 'That image format is not supported (PNG/JPEG/WebP only).'
    if (code === 'file_too_large') return 'That image is too large.'
    if (code === 'invalid_accent_color') return 'Accent color must be a #RRGGBB value.'
    if (code === 'invalid_social_links')
      return 'One of your social handles is invalid (use letters, digits, dots, dashes, underscores).'
    if (code === 'invalid_username') return 'That username is not valid.'
    if (code === 'username_taken') return 'That username is taken.'
    if (code === 'bio_too_long') return 'Bio is too long.'
    return `Save failed (${err.status}).`
  }
  return 'Save failed — please try again.'
}

// Build a PATCH /auth/me payload containing only the fields the user actually changed.
function buildTextDiff(me: MeResponse): UpdateMePayload {
  const out: UpdateMePayload = {}
  if (username.value.trim() !== me.username) out.username = username.value.trim()
  const newBio = bio.value.trim()
  if (newBio !== (me.bio ?? '')) out.bio = newBio === '' ? null : newBio
  const newAccent = accent.value.trim()
  if (newAccent !== (me.accentColor ?? '')) out.accentColor = newAccent === '' ? null : newAccent
  const prev = me.socialLinks
  const same =
    twitch.value === (prev?.twitch ?? '') &&
    youtube.value === (prev?.youtube ?? '') &&
    twitter.value === (prev?.twitter ?? '')
  if (!same) {
    out.socialLinks = {
      twitch: twitch.value,
      youtube: youtube.value,
      twitter: twitter.value,
    }
  }
  return out
}

async function save() {
  if (submitting.value || !auth.user) return
  submitting.value = true
  errorMsg.value = null
  try {
    // 1. Image uploads first so a failed upload doesn't leave the user with a partial save
    //    (text fields applied but image missing). Each upload independently calls /complete
    //    which updates the user row — but those rows aren't visible to other clients until
    //    we await them so total ordering is fine.
    if (avatarFile.value && avatarMime.value) {
      const url = await profileMedia.getAvatarUploadUrl(avatarMime.value)
      await putToPresignedUrl(url.url, avatarFile.value, url.contentType)
      await profileMedia.completeAvatar(url.objectKey)
    }
    if (bannerFile.value && bannerMime.value) {
      const url = await profileMedia.getBannerUploadUrl(bannerMime.value)
      await putToPresignedUrl(url.url, bannerFile.value, url.contentType)
      await profileMedia.completeBanner(url.objectKey)
    }

    // 2. Text fields via PATCH (only if any of them changed).
    const diff = buildTextDiff(auth.user)
    if (Object.keys(diff).length > 0) {
      await updateMe(diff)
    }

    // 3. Refresh the cached me so the parent profile view picks up everything.
    await auth.fetchMe()
    emit('saved')
    emit('close')
  } catch (err) {
    errorMsg.value = friendlyError(err)
    emit('error', errorMsg.value)
  } finally {
    submitting.value = false
  }
}

const inputClass =
  'w-full rounded-md border border-border bg-surface-high px-3.5 py-3 text-sm text-text-primary outline-none placeholder:text-text-muted focus:border-accent transition-colors duration-150'
const labelClass =
  'mb-1.5 block text-[10px] font-bold uppercase tracking-widest text-text-secondary'
const sectionClass = 'flex flex-col gap-2.5'

const currentAvatarUrl = computed(() => avatarPreview.value ?? safeImageUrl(auth.user?.avatarUrl))
const currentBannerUrl = computed(() => bannerPreview.value ?? safeImageUrl(auth.user?.bannerUrl))
</script>

<template>
  <Teleport to="body">
    <Transition
      enter-active-class="transition-opacity duration-200"
      enter-from-class="opacity-0"
      leave-active-class="transition-opacity duration-150"
      leave-to-class="opacity-0"
    >
      <div
        v-if="open"
        class="fixed inset-0 z-50 flex items-center justify-center px-4"
        @click.self="emit('close')"
      >
        <div class="absolute inset-0 bg-black/70" @click="emit('close')" />

        <div
          class="relative z-10 flex max-h-[90vh] w-full max-w-2xl flex-col rounded-lg border border-border-strong bg-surface-raised"
          @click.stop
        >
          <div class="flex items-start justify-between border-b border-border px-5 py-4">
            <div>
              <p
                class="m-0 text-[10px] font-bold uppercase leading-none tracking-[0.14em] text-text-secondary"
              >
                Your profile
              </p>
              <h2
                class="m-0 mt-2 font-condensed text-lg font-extrabold uppercase leading-none tracking-wide text-text-primary"
              >
                Edit profile
              </h2>
            </div>
            <button
              type="button"
              @click="emit('close')"
              aria-label="Close"
              class="inline-flex size-8 shrink-0 cursor-pointer items-center justify-center rounded-lg border border-border text-base leading-none text-text-muted transition-colors duration-150 hover:border-accent hover:text-accent"
            >
              ×
            </button>
          </div>

          <div class="flex flex-col gap-6 overflow-y-auto p-5">
            <!-- ---- Banner preview ---- -->
            <div :class="sectionClass">
              <label :class="labelClass">Banner</label>
              <div
                class="relative h-32 w-full overflow-hidden rounded-lg border border-border bg-surface-high"
              >
                <img
                  v-if="currentBannerUrl"
                  :src="currentBannerUrl"
                  alt="Banner preview"
                  class="h-full w-full object-cover"
                />
                <div v-else class="flex h-full items-center justify-center text-xs text-text-muted">
                  No banner yet — pick a 4:1 image
                </div>
              </div>
              <div class="flex items-center gap-2">
                <label
                  class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-3 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
                >
                  Choose banner…
                  <input type="file" accept="image/*" class="hidden" @change="pickBanner" />
                </label>
                <button
                  v-if="bannerFile"
                  type="button"
                  class="cursor-pointer text-xs font-medium text-text-muted transition-colors duration-150 hover:text-text-primary"
                  @click="clearBannerChoice"
                >
                  Discard pick
                </button>
              </div>
            </div>

            <!-- ---- Avatar preview ---- -->
            <div :class="sectionClass">
              <label :class="labelClass">Avatar</label>
              <div class="flex items-center gap-4">
                <div
                  class="flex h-24 w-24 shrink-0 items-center justify-center overflow-hidden rounded-full border border-border bg-surface-high text-xs text-text-muted"
                >
                  <img
                    v-if="currentAvatarUrl"
                    :src="currentAvatarUrl"
                    alt="Avatar preview"
                    class="h-full w-full object-cover"
                  />
                  <span v-else>No avatar</span>
                </div>
                <div class="flex flex-col gap-2">
                  <label
                    class="cursor-pointer self-start rounded-lg border border-border-strong bg-transparent px-3 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
                  >
                    Choose avatar…
                    <input type="file" accept="image/*" class="hidden" @change="pickAvatar" />
                  </label>
                  <button
                    v-if="avatarFile"
                    type="button"
                    class="self-start cursor-pointer text-xs font-medium text-text-muted transition-colors duration-150 hover:text-text-primary"
                    @click="clearAvatarChoice"
                  >
                    Discard pick
                  </button>
                  <button
                    v-if="canShowResetAvatar"
                    type="button"
                    :disabled="resetBusy"
                    class="self-start cursor-pointer text-xs font-medium text-text-muted transition-colors duration-150 hover:text-accent disabled:opacity-60"
                    @click="resetToOAuthAvatar"
                  >
                    {{ resetBusy ? 'Resetting…' : 'Switch back to my Discord/Google picture' }}
                  </button>
                </div>
              </div>
            </div>

            <!-- ---- Username ---- -->
            <div :class="sectionClass">
              <label :class="labelClass">Username</label>
              <input
                v-model="username"
                maxlength="30"
                :class="inputClass"
                placeholder="@username"
              />
            </div>

            <!-- ---- Bio ---- -->
            <div :class="sectionClass">
              <div class="flex items-baseline justify-between">
                <label :class="labelClass + ' mb-0'">Bio</label>
                <span class="text-[10px] text-text-muted">{{ bio.length }}/500</span>
              </div>
              <textarea
                v-model="bio"
                maxlength="500"
                rows="5"
                placeholder="Tell people about yourself"
                :class="inputClass + ' resize-y min-h-28'"
              />
              <p class="m-0 text-[10px] leading-relaxed text-text-muted">
                Line breaks are kept. **bold**, *italic*, &quot;- &quot; bullets, &quot;1. &quot;
                numbering and [text](https://…) links are rendered.
              </p>
            </div>

            <!-- ---- Accent color ---- -->
            <div :class="sectionClass">
              <label :class="labelClass">Accent color</label>
              <div class="flex flex-wrap items-center gap-3">
                <input
                  type="color"
                  :value="accent || '#00e5a0'"
                  class="h-9 w-12 cursor-pointer rounded-md border border-border bg-transparent"
                  @input="(e) => (accent = (e.target as HTMLInputElement).value.toUpperCase())"
                />
                <input
                  v-model="accent"
                  placeholder="#RRGGBB"
                  maxlength="7"
                  :class="inputClass + ' w-32'"
                />
                <div class="flex flex-wrap gap-1.5">
                  <button
                    v-for="hex in ACCENT_PRESETS"
                    :key="hex"
                    type="button"
                    :title="hex"
                    class="h-7 w-7 cursor-pointer rounded-full border border-border transition-colors duration-150 hover:border-accent"
                    :style="{ background: hex }"
                    @click="accent = hex"
                  />
                </div>
                <button
                  v-if="accent"
                  type="button"
                  class="cursor-pointer text-xs font-medium text-text-muted transition-colors duration-150 hover:text-text-primary"
                  @click="accent = ''"
                >
                  Clear
                </button>
              </div>
            </div>

            <!-- ---- Socials ---- -->
            <div :class="sectionClass">
              <label :class="labelClass">Social links</label>
              <div class="grid gap-2 sm:grid-cols-3">
                <label class="flex items-center gap-2">
                  <span
                    class="w-16 shrink-0 text-[10px] font-bold uppercase tracking-widest text-text-muted"
                    >Twitch</span
                  >
                  <input
                    v-model="twitch"
                    placeholder="@handle"
                    maxlength="32"
                    :class="inputClass"
                  />
                </label>
                <label class="flex items-center gap-2">
                  <span
                    class="w-16 shrink-0 text-[10px] font-bold uppercase tracking-widest text-text-muted"
                    >YouTube</span
                  >
                  <input
                    v-model="youtube"
                    placeholder="@handle"
                    maxlength="32"
                    :class="inputClass"
                  />
                </label>
                <label class="flex items-center gap-2">
                  <span
                    class="w-16 shrink-0 text-[10px] font-bold uppercase tracking-widest text-text-muted"
                    >Twitter</span
                  >
                  <input
                    v-model="twitter"
                    placeholder="@handle"
                    maxlength="32"
                    :class="inputClass"
                  />
                </label>
              </div>
            </div>

            <p
              v-if="errorMsg"
              class="rounded-md border border-accent-border bg-accent-bg px-3 py-2 text-xs font-medium text-accent"
            >
              {{ errorMsg }}
            </p>
          </div>

          <div class="flex justify-end gap-2.5 border-t border-border px-5 py-4">
            <button
              type="button"
              :disabled="hasUploadInFlight"
              @click="emit('close')"
              class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:opacity-60"
            >
              Cancel
            </button>
            <button
              type="button"
              :disabled="hasUploadInFlight"
              @click="save"
              class="cursor-pointer rounded-lg bg-accent px-4 py-1.5 text-xs font-bold text-[#080f0d] transition-[filter] duration-150 hover:brightness-105 disabled:cursor-not-allowed disabled:bg-surface-high disabled:text-text-muted"
            >
              {{ hasUploadInFlight ? 'Saving…' : 'Save' }}
            </button>
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>
