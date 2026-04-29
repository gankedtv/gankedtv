<script setup lang="ts">
import { ref, computed, watch, onBeforeUnmount, useTemplateRef } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Plyr from 'plyr'
import 'plyr/dist/plyr.css'
import { ApiError } from '@/api/client'
import { clips, type ClipDetail } from '@/api/clips'
import { useAuthStore } from '@/stores/auth'
import { safeImageUrl } from '@/lib/url'
import IconHeart from '@/components/icons/IconHeart.vue'
import IconShare from '@/components/icons/IconShare.vue'
import IconMoreVertical from '@/components/icons/IconMoreVertical.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const clip = ref<ClipDetail | null>(null)
const loading = ref(false)
const errored = ref(false)
const liked = ref(false)
const likeCount = ref(0)
const likeBusy = ref(false)
const showToast = ref(false)
const toastText = ref('')

const videoEl = useTemplateRef<HTMLVideoElement>('videoEl')
let player: Plyr | null = null

// Monotonic request counter — guards against A→B→A races where comparing
// `clipId.value === id` would falsely accept the first A response after the
// second A request supersedes it.
let latestLoadId = 0

const clipId = computed(() => {
  const id = route.params.id
  return Array.isArray(id) ? id[0] : id
})

async function loadClip(id: string) {
  const myLoadId = ++latestLoadId
  loading.value = true
  errored.value = false
  clip.value = null
  teardownPlayer()
  try {
    const detail = await clips.getDetail(id)
    if (myLoadId !== latestLoadId) return
    clip.value = detail
    liked.value = detail.likedByMe
    likeCount.value = detail.likeCount
  } catch (err) {
    if (myLoadId !== latestLoadId) return
    if (err instanceof ApiError && err.status === 404) {
      router.replace({ name: 'not-found' })
      return
    }
    errored.value = true
  } finally {
    if (myLoadId === latestLoadId) loading.value = false
  }
}

watch(
  clipId,
  (id) => {
    if (!id) return
    loadClip(id)
  },
  { immediate: true },
)

// Mount Plyr on the <video> element after both the element and the clip data exist.
// We watch both — Vue may render the <video> before the API resolves, or vice versa.
watch(
  [clip, videoEl],
  ([detail, el]) => {
    if (!detail || !el || player) return
    el.src = detail.videoUrl
    player = new Plyr(el, {
      controls: ['play-large', 'play', 'progress', 'current-time', 'mute', 'volume', 'fullscreen'],
      tooltips: { controls: true, seek: true },
    })
  },
  { flush: 'post' },
)

function teardownPlayer() {
  if (player) {
    player.destroy()
    player = null
  }
}

let toastTimer: ReturnType<typeof setTimeout> | null = null
function fireToast(text: string) {
  toastText.value = text
  showToast.value = true
  if (toastTimer !== null) clearTimeout(toastTimer)
  toastTimer = setTimeout(() => {
    showToast.value = false
  }, 2400)
}

onBeforeUnmount(() => {
  teardownPlayer()
  if (toastTimer !== null) clearTimeout(toastTimer)
})

async function toggleLike() {
  if (!clip.value || likeBusy.value) return
  if (!auth.isAuthenticated) {
    router.push({ name: 'login', query: { redirect: route.fullPath } })
    return
  }
  // Optimistic UI: flip locally first, roll back on error so a flaky network doesn't strand
  // the user on a wrong-looking counter.
  const targetId = clip.value.id
  const wasLiked = liked.value
  liked.value = !wasLiked
  likeCount.value += wasLiked ? -1 : 1
  likeBusy.value = true
  try {
    const res = wasLiked ? await clips.unlike(targetId) : await clips.like(targetId)
    // If the user navigated to a different clip while the request was in flight,
    // skip the apply so we don't stamp this clip's count onto the next one.
    if (clip.value?.id !== targetId) return
    liked.value = res.liked
    likeCount.value = res.likeCount
    if (res.liked) fireToast('♥ Added to your liked clips')
  } catch {
    if (clip.value?.id !== targetId) return
    liked.value = wasLiked
    likeCount.value += wasLiked ? 1 : -1
    fireToast('Could not update like — try again')
  } finally {
    likeBusy.value = false
  }
}

async function handleShare() {
  try {
    await navigator.clipboard.writeText(window.location.href)
    fireToast('🔗 Link copied to clipboard')
  } catch {
    fireToast('Copy failed')
  }
}

function formatNum(n: number): string {
  if (n >= 1_000_000) return (n / 1_000_000).toFixed(1) + 'M'
  if (n >= 1_000) return (n / 1_000).toFixed(1) + 'K'
  return String(n)
}

function timeAgo(iso: string): string {
  const diff = (Date.now() - new Date(iso).getTime()) / 1000
  if (diff < 60) return `${Math.floor(diff)}s`
  if (diff < 3600) return `${Math.floor(diff / 60)}m`
  if (diff < 86400) return `${Math.floor(diff / 3600)}h`
  return `${Math.floor(diff / 86400)}d`
}

const initialsFor = (username: string): string =>
  username
    .replace(/[^a-zA-Z]/g, '')
    .slice(0, 2)
    .toUpperCase() || '??'

const authorColor = computed(() => {
  const name = clip.value?.author.username ?? ''
  let hash = 0
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) | 0
  return `hsl(${Math.abs(hash) % 360}, 65%, 45%)`
})

// Hoisted so the template doesn't re-parse the URL on every render.
const authorAvatarUrl = computed(() => safeImageUrl(clip.value?.author.avatarUrl))
</script>

<template>
  <div class="mx-auto max-w-350 px-6 pt-8 pb-30">
    <!-- Loading -->
    <div v-if="loading" class="flex items-center justify-center py-30">
      <span class="font-mono text-sm uppercase tracking-widest text-text-muted">Loading…</span>
    </div>

    <!-- Error -->
    <div v-else-if="errored" class="flex flex-col items-center justify-center gap-3 py-30">
      <span class="font-mono text-sm uppercase tracking-widest text-text-muted">
        Couldn't load this clip.
      </span>
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-raised px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="clipId && loadClip(clipId)"
      >
        Retry
      </button>
    </div>

    <div v-else-if="clip">
      <!-- Breadcrumb -->
      <div
        class="mb-5 flex items-center gap-2 font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted"
      >
        <router-link to="/" class="transition-colors hover:text-text-secondary">Feed</router-link>
        <span>/</span>
        <span>{{ clip.id.slice(0, 8) }}</span>
      </div>

      <!-- Video Player -->
      <div class="overflow-hidden rounded-md border border-border bg-black">
        <video ref="videoEl" controls playsinline class="block aspect-video w-full"></video>
      </div>

      <!-- Title + Meta Row -->
      <div class="mt-5">
        <div v-if="clip.game" class="mb-2">
          <span
            class="rounded-[3px] border border-border-strong bg-surface-base px-2 py-0.75 font-mono text-[10px] font-medium uppercase tracking-[0.06em] text-text-primary"
          >
            {{ clip.game.tag }}
          </span>
          <span class="ml-2 font-mono text-[11px] uppercase tracking-[0.06em] text-text-muted">
            {{ clip.game.name }}
          </span>
        </div>
        <h1
          class="font-heading text-[34px] font-bold leading-[1.05] uppercase tracking-[0.01em] text-text-primary"
        >
          {{ clip.title }}
        </h1>

        <div class="mt-4 flex flex-wrap items-center gap-3">
          <!-- Author info -->
          <div class="flex items-center gap-2">
            <span
              class="inline-flex h-9 w-9 shrink-0 items-center justify-center overflow-hidden rounded-full font-mono text-xs font-semibold text-white"
              :style="{
                background: `linear-gradient(135deg, ${authorColor}, color-mix(in oklab, ${authorColor} 40%, #000))`,
              }"
            >
              <img
                v-if="authorAvatarUrl"
                :src="authorAvatarUrl"
                :alt="clip.author.username"
                class="h-full w-full object-cover"
              />
              <span v-else>{{ initialsFor(clip.author.username) }}</span>
            </span>
            <div>
              <router-link
                :to="`/user/${clip.author.username}`"
                class="font-mono text-[13px] font-semibold text-neon transition-opacity hover:opacity-80"
                >@{{ clip.author.username }}</router-link
              >
              <div class="font-mono text-[10px] tracking-[0.04em] text-text-muted">
                Uploaded {{ timeAgo(clip.createdAt) }} ago
              </div>
            </div>
          </div>

          <div class="flex-1" />

          <!-- Action buttons -->
          <div class="flex items-center gap-2">
            <button
              class="flex items-center gap-1.5 rounded px-3 py-1.5 font-mono text-[12px] transition-all duration-150 disabled:opacity-60"
              :class="
                liked
                  ? 'bg-brand text-white'
                  : 'border border-border bg-surface-raised text-text-secondary'
              "
              :disabled="likeBusy"
              @click="toggleLike"
            >
              <IconHeart :size="14" />
              <span>{{ formatNum(likeCount) }}</span>
            </button>

            <button
              class="flex items-center gap-1.5 rounded border border-border bg-surface-raised px-3 py-1.5 font-mono text-[12px] text-text-secondary transition-all duration-150"
              @click="handleShare"
            >
              <IconShare :size="14" />
              <span>Share</span>
            </button>

            <button
              class="flex h-7 w-7 items-center justify-center rounded text-text-secondary transition-colors hover:bg-surface-raised"
              aria-label="More"
            >
              <IconMoreVertical :size="16" />
            </button>
          </div>
        </div>
      </div>

      <!-- Stat Block -->
      <div class="mt-6 grid grid-cols-3 gap-px overflow-hidden rounded-md bg-border">
        <div
          v-for="stat in [
            { label: 'Plays', value: formatNum(clip.viewCount) },
            { label: 'Likes', value: formatNum(likeCount) },
            { label: 'Uploaded', value: timeAgo(clip.createdAt) + ' ago' },
          ]"
          :key="stat.label"
          class="flex flex-col gap-1 bg-surface-raised px-4 py-3"
        >
          <span class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted">{{
            stat.label
          }}</span>
          <span class="font-heading text-xl font-bold leading-[1.2] text-text-primary">{{
            stat.value
          }}</span>
        </div>
      </div>

      <!-- Description -->
      <div
        v-if="clip.description"
        class="mt-4 rounded-md border border-border bg-surface-raised p-4"
      >
        <div class="mb-2 font-mono text-[10px] uppercase tracking-widest text-text-muted">
          Description
        </div>
        <p class="text-sm leading-[1.6] text-text-secondary">{{ clip.description }}</p>
      </div>
    </div>
  </div>

  <!-- Toast -->
  <Transition
    enter-active-class="animate-[slideUp_0.22s_ease-out_forwards]"
    leave-active-class="animate-[slideDown_0.2s_ease-in_forwards]"
  >
    <div
      v-if="showToast"
      class="fixed bottom-6 left-1/2 z-9999 flex -translate-x-1/2 items-center gap-2 rounded-md border border-brand bg-surface-overlay px-4 py-3 font-mono text-[13px] tracking-[0.04em] whitespace-nowrap text-text-primary shadow-[0_0_20px_var(--color-brand-glow)]"
    >
      {{ toastText }}
    </div>
  </Transition>
</template>
