<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { useRoute } from 'vue-router'
import { CLIPS, USERS, GAMES, COMMENTS, formatNum, formatDuration, clipById } from '@/lib/mock-data'
import UserAvatar from '@/components/UserAvatar.vue'

const route = useRoute()

const clipId = computed(() => {
  const id = route.params.id
  return Array.isArray(id) ? id[0] : id
})

const clip = computed(() => clipById(clipId.value) ?? CLIPS[0])
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
    <div
      class="grid grid-cols-1 items-start gap-7 min-[961px]:grid-cols-[minmax(0,1fr)_340px]"
    >
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
        <div
          class="relative aspect-video overflow-hidden rounded-md border border-border bg-black"
        >
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
              <span
                class="font-mono text-[11px] tracking-[0.06em] text-white/55"
                >{{ clip.id }}</span
              >
            </div>
            <button
              class="flex h-7 w-7 items-center justify-center rounded text-white/70 transition-colors hover:bg-white/10"
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
                <circle cx="8" cy="3" r="1.5" />
                <circle cx="8" cy="8" r="1.5" />
                <circle cx="8" cy="13" r="1.5" />
              </svg>
            </button>
          </div>

          <!-- Center Play/Pause -->
          <button
            class="group absolute inset-0 flex items-center justify-center"
            @click="togglePlay"
          >
            <div
              class="flex h-16 w-16 items-center justify-center rounded-full border-2 border-white/25 bg-black/55 backdrop-blur-xs transition-all duration-150 group-hover:scale-110"
            >
              <!-- Play icon -->
              <svg v-if="!playing" width="24" height="24" viewBox="0 0 24 24" fill="white">
                <path d="M8 5v14l11-7z" />
              </svg>
              <!-- Pause icon -->
              <svg v-else width="24" height="24" viewBox="0 0 24 24" fill="white">
                <path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z" />
              </svg>
            </div>
          </button>

          <!-- Bottom Controls -->
          <div
            class="absolute right-0 bottom-0 left-0 bg-[linear-gradient(to_top,rgba(0,0,0,0.8),transparent)] px-4 pt-6 pb-3"
          >
            <!-- Progress bar -->
            <div
              class="relative mb-3 h-1 cursor-pointer rounded-sm bg-white/15"
              @click="seek"
            >
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
                <svg v-if="!playing" width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                  <path d="M8 5v14l11-7z" />
                </svg>
                <svg v-else width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                  <path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z" />
                </svg>
              </button>

              <!-- Volume -->
              <button class="text-white transition-colors hover:text-text-secondary">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                  <path
                    d="M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 7.97v8.05c1.48-.73 2.5-2.25 2.5-4.02z"
                  />
                </svg>
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
              <button class="text-white transition-colors hover:text-text-secondary">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                  <path
                    d="M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z"
                  />
                </svg>
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
                  <!-- Verified badge -->
                  <svg v-if="user.verified" width="14" height="14" viewBox="0 0 24 24" fill="none">
                    <circle cx="12" cy="12" r="10" fill="var(--color-brand)" />
                    <path
                      d="M9 12l2 2 4-4"
                      stroke="white"
                      stroke-width="2"
                      stroke-linecap="round"
                      stroke-linejoin="round"
                    />
                  </svg>
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
                <svg
                  width="14"
                  height="14"
                  viewBox="0 0 24 24"
                  :fill="liked ? '#fff' : 'currentColor'"
                >
                  <path
                    d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"
                  />
                </svg>
                <span>{{ formatNum(likeCount) }}</span>
              </button>

              <!-- Save -->
              <button
                class="flex items-center gap-1.5 rounded border border-border bg-surface-raised px-3 py-1.5 font-mono text-[12px] text-text-secondary transition-all duration-150"
              >
                <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
                  <path d="M17 3H7c-1.1 0-1.99.9-1.99 2L5 21l7-3 7 3V5c0-1.1-.9-2-2-2z" />
                </svg>
                <span>Save</span>
              </button>

              <!-- Share -->
              <button
                class="flex items-center gap-1.5 rounded border border-border bg-surface-raised px-3 py-1.5 font-mono text-[12px] text-text-secondary transition-all duration-150"
                @click="handleShare"
              >
                <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
                  <path
                    d="M18 16.08c-.76 0-1.44.3-1.96.77L8.91 12.7c.05-.23.09-.46.09-.7s-.04-.47-.09-.7l7.05-4.11A2.99 2.99 0 0 0 18 8a3 3 0 0 0 0-6 3 3 0 0 0 0 6c.34 0 .67-.06.98-.14L12 11.99A2.99 2.99 0 0 0 9 11a3 3 0 0 0 0 6 2.99 2.99 0 0 0 2.98-2.77l7.02 4.12c-.31.08-.63.13-.97.13a3 3 0 0 0 3 3 3 3 0 0 0 0-6z"
                  />
                </svg>
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
            <span
              class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
              >{{ stat.label }}</span
            >
            <span
              class="font-heading text-xl font-bold leading-[1.2] text-text-primary"
              >{{ stat.value }}</span
            >
          </div>
        </div>

        <!-- Description Box -->
        <div class="mt-4 rounded-md border border-border bg-surface-raised p-4">
          <div
            class="mb-2 font-mono text-[10px] uppercase tracking-widest text-text-muted"
          >
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
                  <span class="font-mono text-[12px] font-semibold text-neon"
                    >@{{ c.user }}</span
                  >
                  <span class="font-mono text-[11px] text-text-muted">{{ c.time }}</span>
                </div>
                <p class="mb-2 text-sm leading-normal text-text-secondary">
                  {{ c.text }}
                </p>
                <div class="flex items-center gap-3">
                  <button
                    class="flex items-center gap-1 font-mono text-[11px] text-text-muted transition-colors hover:text-text-secondary"
                  >
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor">
                      <path
                        d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"
                      />
                    </svg>
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
              <span
                class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
              >
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
