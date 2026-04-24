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
  <div style="max-width: 1400px; margin: 0 auto; padding: 32px 24px 120px">
    <div class="clip-layout">
      <!-- LEFT COLUMN -->
      <div>
        <!-- Breadcrumb -->
        <div
          class="flex items-center gap-2 mb-5"
          style="
            font-family: var(--font-mono);
            font-size: 11px;
            color: var(--color-text-muted);
            letter-spacing: 0.08em;
            text-transform: uppercase;
          "
        >
          <router-link to="/" class="hover:text-text-secondary transition-colors">Feed</router-link>
          <span>/</span>
          <span style="color: var(--color-brand-light)">{{ game.name }}</span>
          <span>/</span>
          <span>{{ clip.id }}</span>
        </div>

        <!-- Video Player -->
        <div
          style="
            aspect-ratio: 16/9;
            background: #000;
            border-radius: var(--radius-md);
            overflow: hidden;
            border: 1px solid var(--color-border);
            position: relative;
          "
        >
          <!-- Thumbnail -->
          <img
            :src="clip.art"
            alt="clip thumbnail"
            class="absolute inset-0 w-full h-full object-cover"
            style="opacity: 0.85"
          />

          <!-- Top HUD -->
          <div
            class="absolute top-0 left-0 right-0 flex items-center justify-between px-4 py-3"
            style="background: linear-gradient(to bottom, rgba(0, 0, 0, 0.7), transparent)"
          >
            <div class="flex items-center gap-2">
              <span
                class="px-2 py-0.5 rounded"
                style="
                  background: var(--color-brand);
                  font-family: var(--font-mono);
                  font-size: 10px;
                  letter-spacing: 0.12em;
                  text-transform: uppercase;
                  font-weight: 600;
                "
                >{{ game.tag }}</span
              >
              <span
                style="
                  font-family: var(--font-mono);
                  font-size: 11px;
                  color: rgba(255, 255, 255, 0.55);
                  letter-spacing: 0.06em;
                "
                >{{ clip.id }}</span
              >
            </div>
            <button
              class="flex items-center justify-center rounded w-7 h-7 hover:bg-white/10 transition-colors"
              style="color: rgba(255, 255, 255, 0.7)"
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
            class="absolute inset-0 flex items-center justify-center group"
            @click="togglePlay"
          >
            <div
              class="flex items-center justify-center rounded-full transition-all duration-150 group-hover:scale-110"
              style="
                width: 64px;
                height: 64px;
                background: rgba(0, 0, 0, 0.55);
                border: 2px solid rgba(255, 255, 255, 0.25);
                backdrop-filter: blur(4px);
              "
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
            class="absolute bottom-0 left-0 right-0 px-4 pb-3 pt-6"
            style="background: linear-gradient(to top, rgba(0, 0, 0, 0.8), transparent)"
          >
            <!-- Progress bar -->
            <div
              class="relative mb-3 cursor-pointer"
              style="height: 4px; background: rgba(255, 255, 255, 0.15); border-radius: 2px"
              @click="seek"
            >
              <div
                class="absolute top-0 left-0 h-full rounded"
                :style="{ width: `${progress * 100}%`, background: 'var(--color-brand-light)' }"
              />
              <!-- Scrubber dot -->
              <div
                class="absolute top-1/2 rounded-full"
                :style="{
                  left: `${progress * 100}%`,
                  transform: 'translate(-50%, -50%)',
                  width: '12px',
                  height: '12px',
                  background: 'var(--color-brand-light)',
                  boxShadow: '0 0 6px var(--color-brand-glow)',
                }"
              />
            </div>

            <div class="flex items-center gap-3">
              <!-- Play/Pause mini -->
              <button
                class="text-white hover:text-text-secondary transition-colors"
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
              <button class="text-white hover:text-text-secondary transition-colors">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                  <path
                    d="M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 7.97v8.05c1.48-.73 2.5-2.25 2.5-4.02z"
                  />
                </svg>
              </button>

              <!-- Time -->
              <span
                style="
                  font-family: var(--font-mono);
                  font-size: 11px;
                  color: rgba(255, 255, 255, 0.7);
                "
              >
                {{ currentTime }} / {{ formatDuration(clip.duration) }}
              </span>

              <div class="flex-1" />

              <!-- Speed badge -->
              <span
                class="px-1.5 py-0.5 rounded"
                style="
                  font-family: var(--font-mono);
                  font-size: 10px;
                  background: rgba(255, 255, 255, 0.12);
                  color: rgba(255, 255, 255, 0.7);
                  letter-spacing: 0.05em;
                "
                >1x</span
              >

              <!-- Fullscreen -->
              <button class="text-white hover:text-text-secondary transition-colors">
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
            style="
              font-family: var(--font-heading);
              font-weight: 700;
              font-size: 34px;
              text-transform: uppercase;
              letter-spacing: 0.01em;
              line-height: 1.05;
              color: var(--color-text-primary);
            "
          >
            {{ clip.title }}
          </h1>

          <div class="flex flex-wrap items-center gap-3 mt-4">
            <!-- User info -->
            <div class="flex items-center gap-2">
              <UserAvatar :user="clip.user" :size="36" />
              <div>
                <div class="flex items-center gap-1.5">
                  <router-link
                    :to="`/user/${user.username}`"
                    style="
                      font-family: var(--font-mono);
                      font-size: 13px;
                      font-weight: 600;
                      color: var(--color-neon);
                    "
                    class="hover:opacity-80 transition-opacity"
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
                <div
                  style="
                    font-family: var(--font-mono);
                    font-size: 10px;
                    color: var(--color-text-muted);
                    letter-spacing: 0.04em;
                  "
                >
                  {{ formatNum(user.followers) }} followers · {{ user.clips }} clips
                </div>
              </div>
            </div>

            <!-- Follow button -->
            <button
              class="px-4 py-1.5 rounded transition-all duration-150 text-sm font-semibold"
              :style="
                following
                  ? 'border: 1px solid var(--color-border-strong); color: var(--color-text-secondary); background: transparent; font-family: var(--font-mono); font-size: 12px; letter-spacing: 0.04em;'
                  : 'background: var(--color-brand); color: #fff; border: 1px solid transparent; font-family: var(--font-mono); font-size: 12px; letter-spacing: 0.04em;'
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
                class="flex items-center gap-1.5 px-3 py-1.5 rounded transition-all duration-150"
                :style="
                  liked
                    ? 'background: var(--color-brand); color: #fff; font-family: var(--font-mono); font-size: 12px;'
                    : 'background: var(--color-surface-raised); border: 1px solid var(--color-border); color: var(--color-text-secondary); font-family: var(--font-mono); font-size: 12px;'
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
                class="flex items-center gap-1.5 px-3 py-1.5 rounded transition-all duration-150"
                style="
                  background: var(--color-surface-raised);
                  border: 1px solid var(--color-border);
                  color: var(--color-text-secondary);
                  font-family: var(--font-mono);
                  font-size: 12px;
                "
              >
                <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
                  <path d="M17 3H7c-1.1 0-1.99.9-1.99 2L5 21l7-3 7 3V5c0-1.1-.9-2-2-2z" />
                </svg>
                <span>Save</span>
              </button>

              <!-- Share -->
              <button
                class="flex items-center gap-1.5 px-3 py-1.5 rounded transition-all duration-150"
                style="
                  background: var(--color-surface-raised);
                  border: 1px solid var(--color-border);
                  color: var(--color-text-secondary);
                  font-family: var(--font-mono);
                  font-size: 12px;
                "
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
        <div
          class="grid grid-cols-4 rounded-md mt-6 overflow-hidden"
          style="gap: 1px; background: var(--color-border)"
        >
          <div
            v-for="stat in [
              { label: 'Plays', value: formatNum(clip.views) },
              { label: 'Likes', value: formatNum(likeCount) },
              { label: 'Uploaded', value: clip.createdAt + ' ago' },
              { label: 'Visibility', value: 'Public' },
            ]"
            :key="stat.label"
            class="flex flex-col gap-1 px-4 py-3"
            style="background: var(--color-surface-raised)"
          >
            <span
              style="
                font-family: var(--font-mono);
                font-size: 10px;
                color: var(--color-text-muted);
                letter-spacing: 0.08em;
                text-transform: uppercase;
              "
              >{{ stat.label }}</span
            >
            <span
              style="
                font-family: var(--font-heading);
                font-size: 20px;
                font-weight: 700;
                color: var(--color-text-primary);
                line-height: 1.2;
              "
              >{{ stat.value }}</span
            >
          </div>
        </div>

        <!-- Description Box -->
        <div
          class="rounded-md p-4 mt-4"
          style="background: var(--color-surface-raised); border: 1px solid var(--color-border)"
        >
          <div
            class="mb-2"
            style="
              font-family: var(--font-mono);
              font-size: 10px;
              color: var(--color-text-muted);
              letter-spacing: 0.1em;
              text-transform: uppercase;
            "
          >
            Description
          </div>
          <p class="text-sm mb-3" style="color: var(--color-text-secondary); line-height: 1.6">
            {{ clip.title }}. Ranked match, prime time lobby. Everything was on the line. Check the
            timestamp at peak action — the rotation read was insane.
          </p>
          <div class="flex flex-wrap gap-2">
            <span
              v-for="tag in [game.tag, '#clutch', '#ranked', '#highlights']"
              :key="tag"
              class="px-2 py-0.5 rounded"
              style="
                font-family: var(--font-mono);
                font-size: 11px;
                background: var(--color-surface-overlay);
                border: 1px solid var(--color-border);
                color: var(--color-text-muted);
                letter-spacing: 0.05em;
              "
              >#{{ tag.replace('#', '') }}</span
            >
          </div>
        </div>

        <!-- Comments (Chat) -->
        <div class="mt-8">
          <!-- Section title with brand bar -->
          <div class="flex items-center gap-3 mb-5 section-title-bar">
            <span
              style="
                font-family: var(--font-heading);
                font-size: 18px;
                font-weight: 700;
                text-transform: uppercase;
                letter-spacing: 0.04em;
                color: var(--color-text-primary);
              "
              >Chat</span
            >
            <span
              class="px-2 py-0.5 rounded-full"
              style="
                font-family: var(--font-mono);
                font-size: 11px;
                background: var(--color-surface-raised);
                border: 1px solid var(--color-border);
                color: var(--color-text-muted);
              "
              >{{ COMMENTS.length }}</span
            >
          </div>

          <!-- Comment Input -->
          <div class="flex items-start gap-3 mb-6">
            <UserAvatar user="phantomveil" :size="32" />
            <div class="flex-1 flex gap-2">
              <input
                v-model="comment"
                type="text"
                placeholder="Drop a comment..."
                class="flex-1 rounded-md px-3 py-2 text-sm outline-none transition-colors"
                style="
                  background: var(--color-surface-raised);
                  border: 1px solid var(--color-border);
                  color: var(--color-text-primary);
                  font-family: var(--font-body);
                "
                @focus="
                  ($event.target as HTMLElement).style.borderColor = 'var(--color-border-strong)'
                "
                @blur="($event.target as HTMLElement).style.borderColor = 'var(--color-border)'"
              />
              <button
                class="px-4 py-2 rounded-md text-sm font-semibold transition-all duration-150"
                :style="
                  comment.trim()
                    ? 'background: var(--color-brand); color: #fff; opacity: 1;'
                    : 'background: var(--color-surface-raised); color: var(--color-text-muted); border: 1px solid var(--color-border); opacity: 0.6; cursor: not-allowed;'
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
                <div class="flex items-center gap-2 mb-1">
                  <span
                    style="
                      font-family: var(--font-mono);
                      font-size: 12px;
                      font-weight: 600;
                      color: var(--color-neon);
                    "
                    >@{{ c.user }}</span
                  >
                  <span
                    style="
                      font-family: var(--font-mono);
                      font-size: 11px;
                      color: var(--color-text-muted);
                    "
                    >{{ c.time }}</span
                  >
                </div>
                <p
                  class="text-sm mb-2"
                  style="color: var(--color-text-secondary); line-height: 1.5"
                >
                  {{ c.text }}
                </p>
                <div class="flex items-center gap-3">
                  <button
                    class="flex items-center gap-1 transition-colors hover:text-text-secondary"
                    style="
                      font-family: var(--font-mono);
                      font-size: 11px;
                      color: var(--color-text-muted);
                    "
                  >
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor">
                      <path
                        d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"
                      />
                    </svg>
                    {{ c.likes }}
                  </button>
                  <button
                    class="transition-colors hover:text-text-secondary"
                    style="
                      font-family: var(--font-mono);
                      font-size: 11px;
                      color: var(--color-text-muted);
                    "
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
        <div
          class="rounded-md overflow-hidden"
          style="background: var(--color-surface-raised); border: 1px solid var(--color-border)"
        >
          <div class="px-4 py-3" style="border-bottom: 1px solid var(--color-border)">
            <div class="flex items-center gap-2">
              <span
                class="rounded-full"
                style="
                  width: 7px;
                  height: 7px;
                  background: var(--color-neon);
                  display: inline-block;
                  animation: pulse 2s ease-in-out infinite;
                "
              />
              <span
                style="
                  font-family: var(--font-mono);
                  font-size: 10px;
                  color: var(--color-text-muted);
                  letter-spacing: 0.08em;
                  text-transform: uppercase;
                "
              >
                Up Next · Auto-playing in 00:04
              </span>
            </div>
          </div>

          <!-- Preview of next clip -->
          <div v-if="relatedClips[0]" class="p-3 flex gap-3 items-start">
            <img
              :src="relatedClips[0].art"
              alt="next clip"
              class="rounded object-cover shrink-0"
              style="width: 120px; height: 68px"
            />
            <div class="flex-1 min-w-0">
              <p
                class="text-sm font-semibold mb-1 line-clamp-2"
                style="
                  color: var(--color-text-primary);
                  line-height: 1.35;
                  font-family: var(--font-body);
                "
              >
                {{ relatedClips[0].title }}
              </p>
              <router-link
                :to="`/user/${USERS[relatedClips[0].user].username}`"
                style="font-family: var(--font-mono); font-size: 11px; color: var(--color-neon)"
                class="hover:opacity-80 transition-opacity"
                >@{{ USERS[relatedClips[0].user].username }}</router-link
              >
              <div
                style="
                  font-family: var(--font-mono);
                  font-size: 11px;
                  color: var(--color-text-muted);
                  margin-top: 2px;
                "
              >
                {{ formatNum(relatedClips[0].views) }} plays
              </div>
            </div>
          </div>
        </div>

        <!-- More from [game] -->
        <div>
          <div class="flex items-center gap-3 mb-4 section-title-bar">
            <span
              style="
                font-family: var(--font-mono);
                font-size: 14px;
                font-weight: 600;
                text-transform: uppercase;
                letter-spacing: 0.06em;
                color: var(--color-text-primary);
              "
              >More from {{ game.name }}</span
            >
          </div>

          <div class="flex flex-col gap-1">
            <router-link
              v-for="related in relatedClips.slice(1, 6)"
              :key="related.id"
              :to="`/clip/${related.id}`"
              class="flex gap-3 items-start rounded-md p-2 transition-colors cursor-pointer"
              style="color: inherit; text-decoration: none"
              @mouseenter="
                ($event.currentTarget as HTMLElement).style.background =
                  'var(--color-surface-raised)'
              "
              @mouseleave="($event.currentTarget as HTMLElement).style.background = 'transparent'"
            >
              <img
                :src="related.art"
                alt="related clip"
                class="rounded object-cover shrink-0"
                style="width: 110px; height: 62px"
              />
              <div class="flex-1 min-w-0">
                <p
                  class="text-sm font-semibold mb-1 line-clamp-2"
                  style="
                    color: var(--color-text-primary);
                    line-height: 1.35;
                    font-family: var(--font-body);
                    font-size: 13px;
                  "
                >
                  {{ related.title }}
                </p>
                <span
                  style="font-family: var(--font-mono); font-size: 11px; color: var(--color-neon)"
                  >@{{ USERS[related.user].username }}</span
                >
                <div
                  style="
                    font-family: var(--font-mono);
                    font-size: 11px;
                    color: var(--color-text-muted);
                    margin-top: 2px;
                  "
                >
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
  <Transition name="toast">
    <div
      v-if="showToast"
      class="rounded-md px-4 py-3 flex items-center gap-2"
      style="
        position: fixed;
        bottom: 24px;
        left: 50%;
        transform: translateX(-50%);
        background: var(--color-surface-overlay);
        border: 1px solid var(--color-brand);
        font-family: var(--font-mono);
        font-size: 13px;
        color: var(--color-text-primary);
        letter-spacing: 0.04em;
        z-index: 9999;
        white-space: nowrap;
        box-shadow: 0 0 20px var(--color-brand-glow);
      "
    >
      {{ toastText }}
    </div>
  </Transition>
</template>

<style scoped>
.clip-layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 340px;
  gap: 28px;
  align-items: start;
}

@media (max-width: 960px) {
  .clip-layout {
    grid-template-columns: 1fr;
  }
}

.section-title-bar {
  display: flex;
  align-items: center;
  gap: 12px;
}

.section-title-bar::before {
  content: '';
  width: 4px;
  height: 20px;
  background: var(--color-brand);
  border-radius: 2px;
  display: inline-block;
  flex-shrink: 0;
}

.line-clamp-2 {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.toast-enter-active {
  animation: slideUp 0.22s ease-out forwards;
}

.toast-leave-active {
  animation: slideDown 0.2s ease-in forwards;
}
</style>
