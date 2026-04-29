<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { clips, type ClipFeedItem } from '@/api/clips'
import { formatNum, formatDuration, formatRelativeTime } from '@/lib/format'
import ClipCard from '@/components/ClipCard.vue'
import UserAvatar from '@/components/UserAvatar.vue'
import IconPlay from '@/components/icons/IconPlay.vue'

const router = useRouter()

const items = ref<ClipFeedItem[]>([])
const cursor = ref<string | null>(null)
const loading = ref(false)
// Initial-load failure (no items rendered yet) — full-page error panel.
const errored = ref(false)
// Pagination failure with content already on screen — inline retry on the
// "Load more" button so we don't blow away the loaded feed.
const paginationErrored = ref(false)

// Hero is the newest ready clip; secondary uses the next chunk. The server returns
// items ordered by createdAt desc so position 0 is always the freshest.
const hero = computed(() => items.value[0] ?? null)
const secondary = computed(() => items.value.slice(1, 5))
const grid = computed(() => items.value.slice(5))

async function loadMore() {
  if (loading.value) return
  const isFirstPage = items.value.length === 0
  loading.value = true
  if (isFirstPage) errored.value = false
  paginationErrored.value = false
  try {
    const page = await clips.feed({ cursor: cursor.value, limit: 20 })
    items.value.push(...page.items)
    cursor.value = page.nextCursor
  } catch (err) {
    console.error('feed: load failed', err)
    if (isFirstPage) {
      errored.value = true
    } else {
      paginationErrored.value = true
    }
  } finally {
    loading.value = false
  }
}

onMounted(loadMore)
</script>

<template>
  <main
    class="mx-auto max-w-360 px-6 pt-8 pb-30 max-[899px]:px-3.5 max-[899px]:pt-4 max-[899px]:pb-20"
  >
    <!-- Page header -->
    <div>
      <div
        class="mb-2 flex items-center gap-2 font-mono text-[11px] uppercase tracking-widest text-text-muted"
      >
        <span
          class="h-1.5 w-1.5 shrink-0 rounded-full bg-neon shadow-[0_0_8px_var(--color-neon)] animate-[pulse_2s_infinite]"
        ></span>
        Live Feed · {{ items.length }} clips
      </div>
      <h1
        class="m-0 font-heading text-[clamp(32px,4vw,52px)] font-bold leading-none uppercase tracking-[0.02em] text-text-primary"
      >
        The Feed
      </h1>
    </div>

    <!-- Initial loading state — explicit so the empty-state branch doesn't flash
         in the gap between mount and the first response. -->
    <div
      v-if="loading && items.length === 0 && !errored"
      class="mt-10 flex items-center justify-center py-16"
    >
      <span class="font-mono text-sm uppercase tracking-widest text-text-muted">Loading…</span>
    </div>

    <!-- Empty state -->
    <div
      v-else-if="!loading && items.length === 0 && !errored"
      class="mt-10 flex flex-col items-center justify-center gap-2 rounded-md border border-border bg-surface-raised py-16 text-center"
    >
      <span class="font-mono text-sm uppercase tracking-widest text-text-muted">
        No clips yet — be the first.
      </span>
      <RouterLink
        to="/upload"
        class="rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
      >
        Upload a clip
      </RouterLink>
    </div>

    <!-- Error state -->
    <div
      v-else-if="errored"
      class="mt-10 flex flex-col items-center justify-center gap-2 rounded-md border border-border bg-surface-raised py-16"
    >
      <span class="font-mono text-sm uppercase tracking-widest text-text-muted">
        Couldn't load the feed.
      </span>
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="loadMore"
      >
        Retry
      </button>
    </div>

    <template v-else-if="hero">
      <!-- Desktop hero card -->
      <div
        class="relative mt-7 mb-12 hidden overflow-hidden rounded-lg border border-border bg-surface-raised min-[900px]:block"
      >
        <div class="grid min-h-115 grid-cols-[1.4fr_1fr]">
          <!-- Left: thumbnail -->
          <div class="relative overflow-hidden">
            <img
              v-if="hero.thumbnailKey"
              :src="hero.thumbnailKey"
              alt=""
              class="block h-full w-full object-cover"
            />
            <div v-else class="block h-full w-full bg-surface-sunken" />
            <div
              class="absolute inset-0 bg-[linear-gradient(90deg,transparent_50%,var(--color-surface-raised)_100%)]"
            ></div>
            <!-- Game badge -->
            <div v-if="hero.game" class="absolute top-5 left-5">
              <span
                class="rounded-[3px] border border-border-strong bg-surface-base px-2.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-text-primary"
              >
                {{ hero.game.tag }}
              </span>
            </div>
            <!-- Duration badge -->
            <div v-if="hero.durationSecs !== null" class="absolute bottom-5 left-5">
              <span
                class="rounded bg-black/70 px-2.5 py-1.25 font-mono text-[11px] tracking-[0.06em] text-white backdrop-blur-md"
              >
                {{ formatDuration(hero.durationSecs) }}
              </span>
            </div>
            <button
              class="absolute inset-0 flex cursor-pointer items-center justify-center bg-transparent"
              :aria-label="`Play: ${hero.title}`"
              @click="router.push({ name: 'clip', params: { id: hero.id } })"
            >
              <span
                class="inline-flex h-18 w-18 items-center justify-center rounded-full border border-white/20 bg-black/55 text-white backdrop-blur-md"
              >
                <IconPlay :size="26" />
              </span>
            </button>
          </div>

          <!-- Right: content -->
          <div class="flex flex-col justify-between px-11 py-10">
            <div class="flex flex-col gap-4">
              <div class="font-mono text-[11px] uppercase tracking-[0.15em] text-neon">
                Featured Clip
              </div>
              <h2
                class="m-0 font-heading text-[46px] font-bold leading-none uppercase text-text-primary"
              >
                {{ hero.title }}
              </h2>
              <p class="m-0 max-w-[36ch] text-[15px] leading-normal text-text-secondary">
                <span v-if="hero.game">{{ hero.game.name }} · </span>uploaded
                {{ formatRelativeTime(hero.createdAt) }} ago by
                <span class="text-text-primary">@{{ hero.author.username }}</span>
              </p>
            </div>

            <div class="my-5 flex gap-7 border-y border-border py-4 font-mono">
              <div class="flex flex-col gap-1">
                <span class="text-[10px] uppercase tracking-[0.08em] text-text-muted">Views</span>
                <span class="font-heading text-[22px] font-bold text-text-primary">{{
                  formatNum(hero.viewCount)
                }}</span>
              </div>
              <div class="flex flex-col gap-1">
                <span class="text-[10px] uppercase tracking-[0.08em] text-text-muted">Likes</span>
                <span class="font-heading text-[22px] font-bold text-text-primary">{{
                  formatNum(hero.likeCount)
                }}</span>
              </div>
              <div v-if="hero.durationSecs !== null" class="flex flex-col gap-1">
                <span class="text-[10px] uppercase tracking-[0.08em] text-text-muted"
                  >Duration</span
                >
                <span class="font-heading text-[22px] font-bold text-text-primary">{{
                  formatDuration(hero.durationSecs)
                }}</span>
              </div>
            </div>

            <div class="flex items-center gap-3">
              <button
                class="cursor-pointer rounded-sm border-none bg-brand px-6 py-2.5 font-mono text-[11px] uppercase tracking-[0.12em] text-white transition-colors duration-150 hover:bg-brand-light"
                @click="router.push({ name: 'clip', params: { id: hero.id } })"
              >
                Watch Now
              </button>
              <button
                class="flex cursor-pointer items-center gap-2 rounded-full border border-border bg-surface-overlay py-2 pr-3.5 pl-2 transition-colors duration-150 hover:border-border-hover"
                @click="router.push({ name: 'user', params: { username: hero.author.username } })"
              >
                <UserAvatar :user="hero.author" :size="28" />
                <span class="font-mono text-[11px] tracking-[0.04em] text-text-secondary"
                  >@{{ hero.author.username }}</span
                >
              </button>
            </div>
          </div>
        </div>
      </div>

      <!-- Secondary row (4 cards) -->
      <div v-if="secondary.length" class="mt-7 mb-10">
        <div class="mb-5 flex items-baseline justify-between gap-4">
          <h2
            class="section-title-bar m-0 flex items-center gap-3.5 font-heading text-2xl font-bold uppercase text-text-primary"
          >
            Latest Drops
          </h2>
          <RouterLink
            to="/trending"
            aria-label="See all trending clips"
            class="font-mono text-[11px] uppercase tracking-[0.06em] whitespace-nowrap text-text-secondary no-underline"
            >See All →</RouterLink
          >
        </div>
        <div class="feed-grid">
          <ClipCard
            v-for="clip in secondary"
            :key="clip.id"
            :clip="clip"
            @click="router.push({ name: 'clip', params: { id: clip.id } })"
          />
        </div>
      </div>

      <!-- Main grid -->
      <div v-if="grid.length" class="feed-grid">
        <ClipCard
          v-for="clip in grid"
          :key="clip.id"
          :clip="clip"
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        />
      </div>

      <!-- Load more -->
      <div v-if="cursor || paginationErrored" class="mt-10 flex flex-col items-center gap-2">
        <span
          v-if="paginationErrored"
          class="font-mono text-[11px] uppercase tracking-widest text-text-muted"
        >
          Couldn't load more — try again.
        </span>
        <button
          :disabled="loading"
          @click="loadMore"
          class="cursor-pointer rounded-sm border border-border bg-surface-raised px-6 py-2.5 font-mono text-[11px] uppercase tracking-[0.08em] text-text-primary transition-colors duration-150 hover:border-brand-light disabled:opacity-50"
        >
          {{ loading ? 'Loading…' : paginationErrored ? 'Retry' : 'Load more' }}
        </button>
      </div>
    </template>
  </main>
</template>
