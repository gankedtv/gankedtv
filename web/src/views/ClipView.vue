<script setup lang="ts">
import { ref, computed, watch, watchEffect, onMounted, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { CLIPS, USERS, GAMES, COMMENTS, formatNum, formatDuration, clipById } from '@/lib/mock-data'
import UserAvatar from '@/components/UserAvatar.vue'
import IconPlay from '@/components/icons/IconPlay.vue'
import IconPause from '@/components/icons/IconPause.vue'
import IconHeart from '@/components/icons/IconHeart.vue'
import IconBookmark from '@/components/icons/IconBookmark.vue'
import IconShare from '@/components/icons/IconShare.vue'
import IconMoreVertical from '@/components/icons/IconMoreVertical.vue'
import IconVerifiedBadge from '@/components/icons/IconVerifiedBadge.vue'
import IconVolume from '@/components/icons/IconVolume.vue'
import IconFullscreen from '@/components/icons/IconFullscreen.vue'

const route = useRoute()
const router = useRouter()

const clipId = computed(() => {
  const id = route.params.id
  return Array.isArray(id) ? id[0] : id
})

const resolvedClip = computed(() => (clipId.value ? clipById(clipId.value) : undefined))

watchEffect(() => {
  // Surface unknown clip ids as a real 404 instead of silently falling back to CLIPS[0]
  if (clipId.value && resolvedClip.value === undefined) {
    router.replace({ name: 'not-found' })
  }
})

// Fallback to CLIPS[0] keeps the template safe during the brief tick before the redirect lands
const clip = computed(() => resolvedClip.value ?? CLIPS[0])
const game = computed(() => GAMES[clip.value.game])
const user = computed(() => USERS[clip.value.user])

const relatedClips = computed(() =>
  CLIPS.filter((c) => c.game === clip.value.game && c.id !== clip.value.id).slice(0, 6),
)

// --- State ---
const liked = ref(false)
const likeCount = ref(clip.value.likes)
const playing = ref(true)
const progress = ref(0.32)
const following = ref(false)
const showToast = ref(false)
const toastText = ref('')
const comment = ref('')

// Reset state when clip changes
watch(clip, (newClip) => {
  liked.value = false
  likeCount.value = newClip.likes
  progress.value = 0.32
  playing.value = true
})

// Simulate progress
let intervalId: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  intervalId = setInterval(() => {
    if (playing.value) {
      progress.value = progress.value >= 1 ? 0 : progress.value + 0.003
    }
  }, 100)
})

onUnmounted(() => {
  if (intervalId !== null) clearInterval(intervalId)
})

// --- Actions ---
let toastTimer: ReturnType<typeof setTimeout> | null = null

function fireToast(text: string) {
  toastText.value = text
  showToast.value = true
  if (toastTimer !== null) clearTimeout(toastTimer)
  toastTimer = setTimeout(() => {
    showToast.value = false
  }, 2400)
}

function toggleLike() {
  liked.value = !liked.value
  likeCount.value = liked.value ? likeCount.value + 1 : likeCount.value - 1
  if (liked.value) fireToast('♥ Added to your liked clips')
}

function toggleFollow() {
  following.value = !following.value
  fireToast(
    following.value ? `Following @${user.value.username}` : `Unfollowed @${user.value.username}`,
  )
}

function handleShare() {
  fireToast('🔗 Link copied to clipboard')
}

function togglePlay() {
  playing.value = !playing.value
}

function seek(e: MouseEvent) {
  const bar = e.currentTarget as HTMLElement
  const rect = bar.getBoundingClientRect()
  progress.value = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width))
}

function postComment() {
  if (!comment.value.trim()) return
  comment.value = ''
}

const currentTime = computed(() => {
  const total = clip.value.duration
  const elapsed = Math.floor(progress.value * total)
  const m = Math.floor(elapsed / 60)
  const s = elapsed % 60
  return `${m}:${String(s).padStart(2, '0')}`
})
</script>

<template>
  <div class="mx-auto max-w-350 px-6 pt-8 pb-30">
    <div class="grid grid-cols-1 items-start gap-7 min-[961px]:grid-cols-[minmax(0,1fr)_340px]">
      <!-- LEFT COLUMN -->
      <div>
        <!-- Breadcrumb -->
        <div
          class="mb-5 flex items-center gap-2 font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted"
        >
          <router-link to="/" class="transition-colors hover:text-text-secondary">Feed</router-link>
          <span>/</span>
          <span class="text-brand-light">{{ game.name }}</span>
          <span>/</span>
          <span>{{ clip.id }}</span>
        </div>

        <!-- Video Player -->
        <div class="relative aspect-video overflow-hidden rounded-md border border-border bg-black">
          <!-- Thumbnail -->
          <img
            :src="clip.art"
            alt="clip thumbnail"
            class="absolute inset-0 h-full w-full object-cover opacity-85"
          />

          <!-- Top HUD -->
          <div
            class="absolute top-0 right-0 left-0 flex items-center justify-between bg-[linear-gradient(to_bottom,rgba(0,0,0,0.7),transparent)] px-4 py-3"
          >
            <div class="flex items-center gap-2">
              <span
                class="rounded bg-brand px-2 py-0.5 font-mono text-[10px] font-semibold uppercase tracking-[0.12em]"
                >{{ game.tag }}</span
              >
              <span class="font-mono text-[11px] tracking-[0.06em] text-white/55">{{
                clip.id
              }}</span>
            </div>
            <button
              class="flex h-7 w-7 items-center justify-center rounded text-white/70 transition-colors hover:bg-white/10"
            >
              <IconMoreVertical :size="16" />
            </button>
          </div>

          <!-- Center Play/Pause -->
          <button
            class="group absolute inset-0 flex items-center justify-center"
            @click="togglePlay"
          >
            <div
              class="flex h-16 w-16 items-center justify-center rounded-full border-2 border-white/25 bg-black/55 text-white backdrop-blur-xs transition-all duration-150 group-hover:scale-110"
            >
              <IconPlay v-if="!playing" :size="24" />
              <IconPause v-else :size="24" />
            </div>
          </button>

          <!-- Bottom Controls -->
          <div
            class="absolute right-0 bottom-0 left-0 bg-[linear-gradient(to_top,rgba(0,0,0,0.8),transparent)] px-4 pt-6 pb-3"
          >
            <!-- Progress bar -->
            <div class="relative mb-3 h-1 cursor-pointer rounded-sm bg-white/15" @click="seek">
              <div
                class="absolute top-0 left-0 h-full rounded bg-brand-light"
                :style="{ width: `${progress * 100}%` }"
              />
              <!-- Scrubber dot -->
              <div
                class="absolute top-1/2 h-3 w-3 -translate-x-1/2 -translate-y-1/2 rounded-full bg-brand-light shadow-[0_0_6px_var(--color-brand-glow)]"
                :style="{ left: `${progress * 100}%` }"
              />
            </div>

            <div class="flex items-center gap-3">
              <!-- Play/Pause mini -->
              <button
                class="text-white transition-colors hover:text-text-secondary"
                @click="togglePlay"
              >
                <IconPlay v-if="!playing" :size="18" />
                <IconPause v-else :size="18" />
              </button>

              <!-- Volume -->
              <button
                class="text-white transition-colors hover:text-text-secondary"
                aria-label="Volume"
              >
                <IconVolume :size="18" />
              </button>

              <!-- Time -->
              <span class="font-mono text-[11px] text-white/70">
                {{ currentTime }} / {{ formatDuration(clip.duration) }}
              </span>

              <div class="flex-1" />

              <!-- Speed badge -->
              <span
                class="rounded bg-white/12 px-1.5 py-0.5 font-mono text-[10px] tracking-wider text-white/70"
                >1x</span
              >

              <!-- Fullscreen -->
              <button
                class="text-white transition-colors hover:text-text-secondary"
                aria-label="Fullscreen"
              >
                <IconFullscreen :size="18" />
              </button>
            </div>
          </div>
        </div>

        <!-- Title + Meta Row -->
        <div class="mt-5">
          <h1
            class="font-heading text-[34px] font-bold leading-[1.05] uppercase tracking-[0.01em] text-text-primary"
          >
            {{ clip.title }}
          </h1>

          <div class="mt-4 flex flex-wrap items-center gap-3">
            <!-- User info -->
            <div class="flex items-center gap-2">
              <UserAvatar :user="clip.user" :size="36" />
              <div>
                <div class="flex items-center gap-1.5">
                  <router-link
                    :to="`/user/${user.username}`"
                    class="font-mono text-[13px] font-semibold text-neon transition-opacity hover:opacity-80"
                    >@{{ user.username }}</router-link
                  >
                  <IconVerifiedBadge v-if="user.verified" :size="14" />
                </div>
                <div class="font-mono text-[10px] tracking-[0.04em] text-text-muted">
                  {{ formatNum(user.followers) }} followers · {{ user.clips }} clips
                </div>
              </div>
            </div>

            <!-- Follow button -->
            <button
              class="rounded px-4 py-1.5 font-mono text-[12px] font-semibold tracking-[0.04em] transition-all duration-150"
              :class="
                following
                  ? 'border border-border-strong bg-transparent text-text-secondary'
                  : 'border border-transparent bg-brand text-white'
              "
              @click="toggleFollow"
            >
              {{ following ? 'Following' : 'Follow' }}
            </button>

            <div class="flex-1" />

            <!-- Action buttons right side -->
            <div class="flex items-center gap-2">
              <!-- Like -->
              <button
                class="flex items-center gap-1.5 rounded px-3 py-1.5 font-mono text-[12px] transition-all duration-150"
                :class="
                  liked
                    ? 'bg-brand text-white'
                    : 'border border-border bg-surface-raised text-text-secondary'
                "
                @click="toggleLike"
              >
                <IconHeart :size="14" />
                <span>{{ formatNum(likeCount) }}</span>
              </button>

              <!-- Save -->
              <button
                class="flex items-center gap-1.5 rounded border border-border bg-surface-raised px-3 py-1.5 font-mono text-[12px] text-text-secondary transition-all duration-150"
              >
                <IconBookmark :size="14" />
                <span>Save</span>
              </button>

              <!-- Share -->
              <button
                class="flex items-center gap-1.5 rounded border border-border bg-surface-raised px-3 py-1.5 font-mono text-[12px] text-text-secondary transition-all duration-150"
                @click="handleShare"
              >
                <IconShare :size="14" />
                <span>Share</span>
              </button>
            </div>
          </div>
        </div>

        <!-- Stat Block -->
        <div class="mt-6 grid grid-cols-4 gap-px overflow-hidden rounded-md bg-border">
          <div
            v-for="stat in [
              { label: 'Plays', value: formatNum(clip.views) },
              { label: 'Likes', value: formatNum(likeCount) },
              { label: 'Uploaded', value: clip.createdAt + ' ago' },
              { label: 'Visibility', value: 'Public' },
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

        <!-- Description Box -->
        <div class="mt-4 rounded-md border border-border bg-surface-raised p-4">
          <div class="mb-2 font-mono text-[10px] uppercase tracking-widest text-text-muted">
            Description
          </div>
          <p class="mb-3 text-sm leading-[1.6] text-text-secondary">
            {{ clip.title }}. Ranked match, prime time lobby. Everything was on the line. Check the
            timestamp at peak action — the rotation read was insane.
          </p>
          <div class="flex flex-wrap gap-2">
            <span
              v-for="tag in [game.tag, '#clutch', '#ranked', '#highlights']"
              :key="tag"
              class="rounded border border-border bg-surface-overlay px-2 py-0.5 font-mono text-[11px] tracking-wider text-text-muted"
              >#{{ tag.replace('#', '') }}</span
            >
          </div>
        </div>

        <!-- Comments (Chat) -->
        <div class="mt-8">
          <!-- Section title with brand bar -->
          <div class="section-title-bar mb-5 flex items-center gap-3">
            <span
              class="font-heading text-lg font-bold uppercase tracking-[0.04em] text-text-primary"
              >Chat</span
            >
            <span
              class="rounded-full border border-border bg-surface-raised px-2 py-0.5 font-mono text-[11px] text-text-muted"
              >{{ COMMENTS.length }}</span
            >
          </div>

          <!-- Comment Input -->
          <div class="mb-6 flex items-start gap-3">
            <UserAvatar user="phantomveil" :size="32" />
            <div class="flex flex-1 gap-2">
              <input
                v-model="comment"
                type="text"
                placeholder="Drop a comment..."
                class="flex-1 rounded-md border border-border bg-surface-raised px-3 py-2 font-body text-sm text-text-primary outline-none transition-colors focus:border-border-strong"
              />
              <button
                class="rounded-md px-4 py-2 text-sm font-semibold transition-all duration-150"
                :class="
                  comment.trim()
                    ? 'bg-brand text-white opacity-100'
                    : 'cursor-not-allowed border border-border bg-surface-raised text-text-muted opacity-60'
                "
                :disabled="!comment.trim()"
                @click="postComment"
              >
                Post
              </button>
            </div>
          </div>

          <!-- Comment List -->
          <div class="flex flex-col gap-5">
            <div v-for="(c, i) in COMMENTS" :key="i" class="flex items-start gap-3">
              <UserAvatar
                :user="Object.keys(USERS).find((k) => USERS[k].username === c.user) ?? c.user"
                :size="32"
              />
              <div class="flex-1">
                <div class="mb-1 flex items-center gap-2">
                  <span class="font-mono text-[12px] font-semibold text-neon">@{{ c.user }}</span>
                  <span class="font-mono text-[11px] text-text-muted">{{ c.time }}</span>
                </div>
                <p class="mb-2 text-sm leading-normal text-text-secondary">
                  {{ c.text }}
                </p>
                <div class="flex items-center gap-3">
                  <button
                    class="flex items-center gap-1 font-mono text-[11px] text-text-muted transition-colors hover:text-text-secondary"
                  >
                    <IconHeart :size="12" />
                    {{ c.likes }}
                  </button>
                  <button
                    class="font-mono text-[11px] text-text-muted transition-colors hover:text-text-secondary"
                  >
                    Reply
                  </button>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- RIGHT RAIL (sidebar) -->
      <div class="flex flex-col gap-6">
        <!-- Up Next Panel -->
        <div class="overflow-hidden rounded-md border border-border bg-surface-raised">
          <div class="border-b border-border px-4 py-3">
            <div class="flex items-center gap-2">
              <span
                class="inline-block h-1.75 w-1.75 rounded-full bg-neon animate-[pulse_2s_ease-in-out_infinite]"
              />
              <span class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted">
                Up Next · Auto-playing in 00:04
              </span>
            </div>
          </div>

          <!-- Preview of next clip -->
          <div v-if="relatedClips[0]" class="flex items-start gap-3 p-3">
            <img
              :src="relatedClips[0].art"
              alt="next clip"
              class="h-17 w-30 shrink-0 rounded object-cover"
            />
            <div class="min-w-0 flex-1">
              <p
                class="mb-1 line-clamp-2 font-body text-sm font-semibold leading-[1.35] text-text-primary"
              >
                {{ relatedClips[0].title }}
              </p>
              <router-link
                :to="`/user/${USERS[relatedClips[0].user].username}`"
                class="font-mono text-[11px] text-neon transition-opacity hover:opacity-80"
                >@{{ USERS[relatedClips[0].user].username }}</router-link
              >
              <div class="mt-0.5 font-mono text-[11px] text-text-muted">
                {{ formatNum(relatedClips[0].views) }} plays
              </div>
            </div>
          </div>
        </div>

        <!-- More from [game] -->
        <div>
          <div class="section-title-bar mb-4 flex items-center gap-3">
            <span
              class="font-mono text-[14px] font-semibold uppercase tracking-[0.06em] text-text-primary"
              >More from {{ game.name }}</span
            >
          </div>

          <div class="flex flex-col gap-1">
            <router-link
              v-for="related in relatedClips.slice(1, 6)"
              :key="related.id"
              :to="`/clip/${related.id}`"
              class="flex cursor-pointer items-start gap-3 rounded-md p-2 text-inherit no-underline transition-colors hover:bg-surface-raised"
            >
              <img
                :src="related.art"
                alt="related clip"
                class="h-15.5 w-27.5 shrink-0 rounded object-cover"
              />
              <div class="min-w-0 flex-1">
                <p
                  class="mb-1 line-clamp-2 font-body text-[13px] font-semibold leading-[1.35] text-text-primary"
                >
                  {{ related.title }}
                </p>
                <span class="font-mono text-[11px] text-neon"
                  >@{{ USERS[related.user].username }}</span
                >
                <div class="mt-0.5 font-mono text-[11px] text-text-muted">
                  {{ formatNum(related.views) }} plays
                </div>
              </div>
            </router-link>
          </div>
        </div>
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
