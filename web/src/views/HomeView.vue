<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { clips, type ClipFeedItem } from '@/api/clips'
import { useAuthStore } from '@/stores/auth'
import { formatNum, formatDuration, formatRelativeTime } from '@/lib/format'
import ClipCard from '@/components/ClipCard.vue'
import UserAvatar from '@/components/UserAvatar.vue'
import GameTag from '@/components/GameTag.vue'
import TagChip from '@/components/TagChip.vue'
import DurationBadge from '@/components/DurationBadge.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'
import LoadMoreButton from '@/components/LoadMoreButton.vue'
import IconPlay from '@/components/icons/IconPlay.vue'

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()

type FeedSource = 'public' | 'following'
const TABS: { key: FeedSource; label: string }[] = [
  { key: 'public', label: 'Latest' },
  { key: 'following', label: 'Following' },
]

// Honour ?tab=following (used after login to bounce a viewer back to the tab they
// clicked while signed-out). Gated on auth so a signed-out user landing on
// `/?tab=following` directly doesn't hit a 401 and the generic error panel — they
// fall through to public, which is the right initial state for anonymous browsing.
const initialTab: FeedSource =
  route.query.tab === 'following' && auth.isAuthenticated ? 'following' : 'public'
const source = ref<FeedSource>(initialTab)

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

const showFollowingEmpty = computed(
  () =>
    source.value === 'following' && !loading.value && !errored.value && items.value.length === 0,
)

async function loadMore() {
  if (loading.value) return
  const isFirstPage = items.value.length === 0
  loading.value = true
  if (isFirstPage) errored.value = false
  paginationErrored.value = false
  // Capture the source at request time so a tab switch mid-flight doesn't drop the
  // response into the wrong list.
  const requestedSource = source.value
  try {
    const page = await clips.feed({
      cursor: cursor.value,
      limit: 20,
      source: requestedSource,
    })
    if (source.value !== requestedSource) return
    items.value.push(...page.items)
    cursor.value = page.nextCursor
  } catch (err) {
    if (source.value !== requestedSource) return
    console.error('feed: load failed', err)
    if (isFirstPage) {
      errored.value = true
    } else {
      paginationErrored.value = true
    }
  } finally {
    if (source.value === requestedSource) loading.value = false
  }
}

function selectTab(next: FeedSource) {
  if (next === source.value) return
  // Signed-out users can browse public but not following — bounce through /login with
  // a tab=following hint so they land back here after auth.
  if (next === 'following' && !auth.isAuthenticated) {
    router.push({ name: 'login', query: { redirect: '/?tab=following' } })
    return
  }
  source.value = next
  items.value = []
  cursor.value = null
  errored.value = false
  paginationErrored.value = false
  // Release ownership of the loading flag before triggering the new fetch.
  // Without this, the loadMore() call below would early-return at its
  // `if (loading.value) return` guard (a prior in-flight fetch for the old
  // source has loading=true). That prior fetch's drift-detected early-return
  // then never clears loading, leaving the UI stuck in a loading state forever.
  // The in-flight request will discard its response via the source check, so
  // dropping the flag here is safe.
  loading.value = false
  loadMore()
}

onMounted(loadMore)
</script>

<template>
  <main
    class="mx-auto max-w-360 px-6 pt-8 pb-30 max-[899px]:px-3.5 max-[899px]:pt-4 max-[899px]:pb-20"
  >
    <PageHeader title="The Feed" pulse>
      <template #caption>Live Feed · {{ items.length }} clips</template>
    </PageHeader>

    <UnderlineTabs class="mt-6" :tabs="TABS" :active="source" @select="selectTab" />

    <!-- Initial loading state — explicit so the empty-state branch doesn't flash
         in the gap between mount and the first response. -->
    <StatusPanel
      v-if="loading && items.length === 0 && !errored"
      kind="loading"
      message="Loading…"
    />

    <!-- Empty state — Following gets its own CTA per the issue spec; Latest falls
         through to the original "no clips yet — be the first" path. -->
    <StatusPanel
      v-else-if="showFollowingEmpty"
      kind="empty"
      message="Follow some creators to fill your Following feed."
    >
      <div class="flex flex-wrap items-center justify-center gap-2">
        <button
          class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
          @click="selectTab('public')"
        >
          Browse Latest
        </button>
        <RouterLink
          to="/games"
          class="rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        >
          Explore games
        </RouterLink>
      </div>
    </StatusPanel>

    <StatusPanel
      v-else-if="!loading && items.length === 0 && !errored"
      kind="empty"
      message="No clips yet — be the first."
    >
      <RouterLink
        to="/upload"
        class="rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
      >
        Upload a clip
      </RouterLink>
    </StatusPanel>

    <!-- Error state -->
    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load the feed.">
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-overlay px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="loadMore"
      >
        Retry
      </button>
    </StatusPanel>

    <template v-else-if="hero">
      <!-- Desktop hero card -->
      <div
        class="relative mt-7 mb-12 hidden overflow-hidden rounded-lg border border-border bg-surface-raised min-[900px]:block"
      >
        <div class="grid min-h-115 grid-cols-[1.4fr_1fr]">
          <!-- Left: thumbnail -->
          <div class="relative overflow-hidden">
            <img :src="hero.thumbnailUrl" alt="" class="block h-full w-full object-cover" />
            <div
              class="absolute inset-0 bg-[linear-gradient(90deg,transparent_50%,var(--color-surface-raised)_100%)]"
            ></div>
            <!-- Game badge -->
            <div v-if="hero.game" class="absolute top-5 left-5">
              <GameTag :tag="hero.game.tag" size="md" />
            </div>
            <!-- Duration badge -->
            <DurationBadge
              :seconds="hero.durationSecs"
              size="md"
              class="absolute bottom-5 left-5"
            />
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
                <AuthorHandle :username="hero.author.username" class="text-text-primary" />
              </p>
              <div v-if="hero.tags.length" class="flex flex-wrap gap-2">
                <TagChip
                  v-for="t in hero.tags"
                  :key="t.id"
                  :slug="t.slug"
                  :name="t.name"
                  size="md"
                />
              </div>
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
                <AuthorHandle
                  :username="hero.author.username"
                  class="text-[11px] tracking-[0.04em] text-text-secondary"
                />
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

      <LoadMoreButton
        v-if="cursor || paginationErrored"
        class="mt-10"
        :loading="loading"
        :errored="paginationErrored"
        @load="loadMore"
      />
    </template>
  </main>
</template>
