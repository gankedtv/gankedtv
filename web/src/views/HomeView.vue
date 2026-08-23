<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { clips, type ClipFeedItem } from '@/api/clips'
import { games as gamesApi, type GameListItem } from '@/api/games'
import { useAuthStore } from '@/stores/auth'
import { usePresenceStore } from '@/stores/presence'
import { formatNum, formatRelativeTime } from '@/lib/format'
import ClipCard from '@/components/ClipCard.vue'
import UserAvatar from '@/components/UserAvatar.vue'
import GameTag from '@/components/GameTag.vue'
import DurationBadge from '@/components/DurationBadge.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import GameCoverTile from '@/components/GameCoverTile.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'
import LoadMoreButton from '@/components/LoadMoreButton.vue'
import ReelsFab from '@/components/reels/ReelsFab.vue'
import IconPlay from '@/components/icons/IconPlay.vue'
import ThumbImage from '@/components/ThumbImage.vue'

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()

// Hero "Live now" panel — AppNav owns the presence poll lifetime; this view only
// reads the store. Auth-only and v-if-gated: what the API doesn't serve isn't rendered.
const presenceStore = usePresenceStore()
const LIVE_PANEL_AVATAR_CAP = 6
const liveFollows = computed(() =>
  auth.isAuthenticated ? presenceStore.followsOnline.slice(0, LIVE_PANEL_AVATAR_CAP) : [],
)
const liveFollowsOverflow = computed(() =>
  Math.max(
    0,
    (auth.isAuthenticated ? presenceStore.followsOnlineCount : 0) - liveFollows.value.length,
  ),
)

type FeedSource = 'following' | 'for-you'
type HomeTab = FeedSource | 'trending' | 'top-rated'
// "For You" is the personalized feed: tiered by followed authors + liked games for
// signed-in users, transparent global latest for anonymous. "Top Rated" renders disabled
// until the API grows a likes-weighted sort.
const TABS: { key: HomeTab; label: string; to?: { name: string }; disabled?: boolean }[] = [
  { key: 'for-you', label: 'For You' },
  { key: 'following', label: 'Following' },
  { key: 'trending', label: 'Trending', to: { name: 'trending' } },
  { key: 'top-rated', label: 'Top Rated', disabled: true },
]

// Honour ?tab=following (used after login to bounce a viewer back to the tab they
// clicked while signed-out). Gated on auth so a signed-out user landing on
// `/?tab=following` directly doesn't hit a 401 and the generic error panel — they
// fall through to For You, the default for anonymous browsing (For You serves global
// latest when signed-out, so it's a safe anonymous landing tab).
const initialTab: FeedSource =
  route.query.tab === 'following' && auth.isAuthenticated ? 'following' : 'for-you'
const source = ref<FeedSource>(initialTab)

const items = ref<ClipFeedItem[]>([])
const cursor = ref<string | null>(null)
const loading = ref(false)
// Daily "Clip of the Day" pick. Loaded once on mount in parallel with the feed.
// Survives tab switches because it's a global pick, not per-source.
const featured = ref<ClipFeedItem | null>(null)
// Initial-load failure (no items rendered yet) — full-page error panel.
const errored = ref(false)
// Pagination failure with content already on screen — inline retry on the
// "Load more" button so we don't blow away the loaded feed.
const paginationErrored = ref(false)

// Side-loads for the Top Games / Trending bands. Silent failure — the band simply
// doesn't render, the feed is never blocked on them.
const bandGames = ref<GameListItem[]>([])
const bandTrending = ref<ClipFeedItem[]>([])

// Active game filter (the pills below the tabs). null = All. Seeded from ?game= so a
// filtered view survives reload and is shareable. Resolved against bandGames — a ?game=
// outside the pill set falls back to unfiltered (see onMounted).
const initialGameSlug = typeof route.query.game === 'string' ? route.query.game : null
const activeGameSlug = ref<string | null>(initialGameSlug)
const activeGame = computed(
  () => bandGames.value.find((g) => g.slug === activeGameSlug.value) ?? null,
)
// True only while a deep-linked ?game= slug is being resolved against bandGames before the
// first (game-scoped) fetch is dispatched. Keeps the loading panel up instead of flashing the
// empty state, since loadMore() — the only thing that sets `loading` — is deferred until then.
const resolvingGameFilter = ref(!!initialGameSlug)
// Stable key for the active (source, game) filter — loadMore captures it so a tab/pill
// switch mid-flight discards the now-stale response instead of dropping it into the new list.
const filterKey = computed(() => `${source.value}:${activeGameSlug.value ?? ''}`)

// Hero prefers today's featured pick (computed server-side via /clips/featured) and falls
// back to items[0] (newest ready clip) so the hero never goes blank. Under an active game
// filter it uses items[0] instead — the global Clip of the Day is a different game and would
// contradict the filter.
const hero = computed<ClipFeedItem | null>(
  () => (activeGameSlug.value ? items.value[0] : (featured.value ?? items.value[0])) ?? null,
)
// True only when the hero is actually today's featured pick — the badge must not lie about
// provenance, and a filtered hero is never the featured pick.
const heroIsFeatured = computed(() => !activeGameSlug.value && featured.value !== null)
// The hero isn't always items[0] — a featured "Clip of the Day" sits outside the
// feed ordering. Slicing the bands from a fixed offset would drop the newest clip
// and double-render the featured pick, so exclude the hero clip by id instead.
const rest = computed(() => {
  const heroId = hero.value?.id
  return heroId ? items.value.filter((c) => c.id !== heroId) : items.value
})
// Hero band right rail — ranked top-clips list.
const latestDrops = computed(() => rest.value.slice(0, 5))
// Recent Clips band — everything else, freshest first.
const feed = computed(() => rest.value.slice(5))

// Trending band — feature + four runner-ups.
const trendingFeature = computed(() => bandTrending.value[0] ?? null)
const trendingList = computed(() => bandTrending.value.slice(1, 5))

const showFollowingEmpty = computed(
  () =>
    source.value === 'following' && !loading.value && !errored.value && items.value.length === 0,
)
// A game filter that legitimately returns nothing (e.g. Following ∩ a game the followed
// creators haven't posted, or a rare race where a pill game's last clip vanished). Gets its
// own message + a "Clear filter" escape instead of the misleading "be the first" / Following
// panels, and takes priority over both since the filter is the specific cause.
const showGameFilterEmpty = computed(
  () => !!activeGameSlug.value && !loading.value && !errored.value && items.value.length === 0,
)

async function loadMore() {
  if (loading.value) return
  const isFirstPage = items.value.length === 0
  loading.value = true
  if (isFirstPage) errored.value = false
  paginationErrored.value = false
  // Capture the filter (source + game) at request time so a tab/pill switch mid-flight
  // doesn't drop the response into the wrong list.
  const requestedKey = filterKey.value
  const requestedSource = source.value
  const requestedGameId = activeGame.value?.id
  try {
    const page = await clips.feed({
      cursor: cursor.value,
      limit: 20,
      source: requestedSource,
      gameId: requestedGameId,
    })
    if (filterKey.value !== requestedKey) return
    items.value.push(...page.items)
    cursor.value = page.nextCursor
  } catch (err) {
    if (filterKey.value !== requestedKey) return
    console.error('feed: load failed', err)
    if (isFirstPage) {
      errored.value = true
    } else {
      paginationErrored.value = true
    }
  } finally {
    if (filterKey.value === requestedKey) loading.value = false
  }
}

function onTabSelect(next: HomeTab) {
  // Link tabs (Trending) navigate on their own; disabled tabs never emit. Only the two
  // local-state feed tabs (For You + Following) reach selectTab.
  if (next !== 'for-you' && next !== 'following') return
  selectTab(next)
}

// Reset the feed to page one and refetch under the current filter. Releasing the loading
// flag first is deliberate: a prior in-flight fetch (for the old filter) leaves loading=true,
// which would otherwise make the loadMore() below early-return at its `if (loading.value)`
// guard — and that stale fetch's drift-detected early-return never clears the flag, wedging
// the UI in a loading state. The stale request discards its response via the filterKey
// check, so dropping the flag here is safe.
function reloadFeed() {
  items.value = []
  cursor.value = null
  errored.value = false
  paginationErrored.value = false
  loading.value = false
  loadMore()
}

function selectTab(next: FeedSource) {
  if (next === source.value) return
  // Signed-out users can browse For You + Latest but not Following — bounce through /login
  // with a tab=following hint so they land back here after auth.
  if (next === 'following' && !auth.isAuthenticated) {
    router.push({ name: 'login', query: { redirect: '/?tab=following' } })
    return
  }
  source.value = next
  reloadFeed()
}

function selectGame(slug: string | null) {
  if (slug === activeGameSlug.value) return
  activeGameSlug.value = slug
  syncGameQuery(slug)
  reloadFeed()
}

// Reflect the active game in ?game= so a filtered view is shareable and survives reload.
// replace (not push) so toggling pills doesn't stack history entries; other query params
// (e.g. ?tab=) are preserved.
function syncGameQuery(slug: string | null) {
  const query = { ...route.query }
  if (slug) query.game = slug
  else delete query.game
  void router.replace({ query })
}

const PILL_BASE =
  'cursor-pointer rounded-full border px-3 py-1 text-[11px] font-semibold transition-colors duration-150'
function pillClasses(active: boolean) {
  return [
    PILL_BASE,
    active
      ? 'border-accent-border bg-accent-bg text-accent'
      : 'border-border text-text-muted hover:border-accent-border hover:text-accent',
  ]
}

async function loadFeatured() {
  try {
    featured.value = await clips.featured()
  } catch (err) {
    // Silent failure — hero falls back to items[0]. No user-facing error.
    console.error('featured: load failed', err)
    featured.value = null
  }
}

async function loadBandGames() {
  try {
    bandGames.value = (await gamesApi.list(5, { hasClips: true })).slice(0, 5)
  } catch (err) {
    console.error('home: games band load failed', err)
    bandGames.value = []
  }
}

async function loadBandTrending() {
  try {
    const page = await clips.feed({ sort: 'trending', window: '24h', limit: 5 })
    bandTrending.value = page.items
  } catch (err) {
    console.error('home: trending band load failed', err)
    bandTrending.value = []
  }
}

function openClip(id: string) {
  router.push({ name: 'clip', params: { id } })
}

onMounted(() => {
  const gamesLoaded = loadBandGames()
  if (activeGameSlug.value) {
    // Deep-linked to ?game=: resolve the id from the band list before the first fetch so the
    // initial page is already filtered (no unfiltered flash). If the slug isn't a pill game,
    // drop the stale filter and load unfiltered.
    void loadFeatured()
    void loadBandTrending()
    void gamesLoaded.then(() => {
      if (activeGameSlug.value && !activeGame.value) {
        activeGameSlug.value = null
        syncGameQuery(null)
      }
      // Hand the loading panel back to loadMore() (which sets `loading` synchronously) in the
      // same tick, so there's no frame where both flags are false and the empty state shows.
      resolvingGameFilter.value = false
      void loadMore()
    })
  } else {
    // Common path: fire the feed first (don't wait on the games list), then the side bands.
    void loadMore()
    void loadFeatured()
    void loadBandTrending()
  }
})
</script>

<template>
  <main class="mx-auto max-w-300 px-7 pt-7 pb-16 max-tablet:px-4 max-tablet:pt-4">
    <!-- Initial loading state — explicit so the empty-state branch doesn't flash in the gap
         between mount and the first response, including while a deep-linked ?game= resolves. -->
    <StatusPanel
      v-if="(loading || resolvingGameFilter) && items.length === 0 && !errored"
      kind="loading"
      message="Loading"
    />

    <!-- Active game filter returned nothing — dedicated message + a way to clear it, rather
         than the generic "be the first" panel implying the whole site is empty. -->
    <StatusPanel
      v-else-if="showGameFilterEmpty"
      kind="empty"
      :message="`No clips for ${activeGame?.name ?? 'this game'} yet.`"
    >
      <button
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="selectGame(null)"
      >
        Clear filter
      </button>
    </StatusPanel>

    <!-- Empty state — Following gets its own CTA per the issue spec; For You falls
         through to the original "no clips yet — be the first" path. -->
    <StatusPanel
      v-else-if="showFollowingEmpty"
      kind="empty"
      message="Follow some creators to fill your Following feed."
    >
      <div class="flex flex-wrap items-center justify-center gap-2">
        <button
          class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
          @click="selectTab('for-you')"
        >
          Browse clips
        </button>
        <RouterLink
          to="/games"
          class="rounded-lg border border-border-strong px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
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
        class="rounded-lg border border-border-strong px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
      >
        Upload a clip
      </RouterLink>
    </StatusPanel>

    <!-- Error state -->
    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load the feed.">
      <button
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="loadMore"
      >
        Retry
      </button>
    </StatusPanel>

    <template v-else-if="hero">
      <!-- ====== Feed controls — tabs + game filter pills ====== -->
      <div class="flex items-center gap-4 border-b border-border">
        <UnderlineTabs class="border-b-0" :tabs="TABS" :active="source" @select="onTabSelect" />
        <!-- Pills filter the feed in place (Arena rule, no extra click). "All" resets. -->
        <div
          v-if="bandGames.length"
          class="ml-auto flex flex-wrap items-center justify-end gap-1.5 pb-2 max-tablet:hidden"
          role="group"
          aria-label="Filter by game"
        >
          <button
            type="button"
            :class="pillClasses(activeGameSlug === null)"
            :aria-pressed="activeGameSlug === null"
            @click="selectGame(null)"
          >
            All
          </button>
          <button
            v-for="g in bandGames"
            :key="g.id"
            type="button"
            :class="pillClasses(activeGameSlug === g.slug)"
            :aria-pressed="activeGameSlug === g.slug"
            @click="selectGame(g.slug)"
          >
            {{ g.tag }}
          </button>
        </div>
      </div>

      <!-- ====== Hero band — featured clip + ranked sidebar ====== -->
      <section class="mt-5">
        <div class="grid grid-cols-[1fr_300px] items-start gap-5 max-lg:grid-cols-1">
          <!-- Hero — full-bleed thumbnail, overlay title (board treatment 1d) -->
          <article
            class="group relative aspect-video min-w-0 cursor-pointer overflow-hidden rounded-lg border border-border bg-surface-high transition-colors duration-150 hover:border-border-strong"
            @click="openClip(hero.id)"
          >
            <ThumbImage
              :src="hero.thumbnailUrl"
              eager
              class="absolute inset-0 h-full w-full object-cover"
            />
            <!-- The one sanctioned gradient: thumbnail legibility overlay. -->
            <div
              class="absolute inset-x-0 bottom-0 h-[64%] bg-[linear-gradient(transparent,rgba(0,0,0,0.88))]"
            ></div>
            <div v-if="hero.game" class="absolute top-3 left-3" @click.stop>
              <RouterLink
                :to="{ name: 'game-detail', params: { slug: hero.game.slug } }"
                :aria-label="`Browse ${hero.game.name} clips`"
              >
                <GameTag :tag="hero.game.tag" size="md" />
              </RouterLink>
            </div>
            <DurationBadge :seconds="hero.durationSecs" size="md" class="absolute top-3 right-3" />
            <button
              class="absolute inset-0 flex cursor-pointer items-center justify-center bg-transparent"
              :aria-label="`Play: ${hero.title}`"
              @click.stop="openClip(hero.id)"
            >
              <span
                class="inline-flex size-14 items-center justify-center rounded-full border border-white/30 bg-black/55 text-[#f4f1e8] transition-colors duration-150 group-hover:bg-black/70"
              >
                <IconPlay :size="22" />
              </span>
            </button>
            <div class="pointer-events-none absolute inset-x-5 bottom-4">
              <p class="m-0 mb-1.5 text-[10px] font-bold uppercase tracking-[0.14em] text-accent">
                {{ heroIsFeatured ? 'Clip of the Day' : 'Featured Clip' }}
              </p>
              <h2
                class="m-0 mb-2 font-condensed text-[clamp(22px,2.6vw,34px)] font-black uppercase leading-[0.98] text-[#f4f1e8]"
              >
                {{ hero.title }}
              </h2>
              <div
                class="pointer-events-auto flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-[#f4f1e8]/80"
              >
                <AuthorHandle
                  :username="hero.author.username"
                  as="link"
                  class="font-semibold text-accent"
                  @click.stop
                />
                <span class="text-[#f4f1e8]/50">·</span>
                <span>{{ formatNum(hero.viewCount) }} views</span>
                <span class="text-[#f4f1e8]/50">·</span>
                <span>{{ formatNum(hero.likeCount) }} likes</span>
                <span class="text-[#f4f1e8]/50">·</span>
                <span>{{ formatRelativeTime(hero.createdAt) }}</span>
              </div>
            </div>
          </article>

          <!-- Ranked sidebar — top clips 01–05 -->
          <aside v-if="latestDrops.length" class="min-w-0">
            <p class="m-0 text-[10px] font-bold uppercase tracking-[0.12em] text-text-muted">
              Top clips today
            </p>
            <ol data-testid="latest-drops" class="m-0 mt-1 flex list-none flex-col p-0">
              <li
                v-for="(clip, i) in latestDrops"
                :key="clip.id"
                class="group grid cursor-pointer grid-cols-[36px_56px_1fr] items-center gap-2.5 border-b border-border py-2.5"
                role="link"
                tabindex="0"
                :aria-label="`Open clip: ${clip.title}`"
                @click="openClip(clip.id)"
                @keydown.enter.self="openClip(clip.id)"
                @keydown.space.self.prevent="openClip(clip.id)"
              >
                <span
                  class="font-condensed text-[22px] font-black leading-none"
                  :class="i === 0 ? 'text-accent' : 'text-text-muted'"
                >
                  {{ String(i + 1).padStart(2, '0') }}
                </span>
                <span
                  class="relative block aspect-video overflow-hidden rounded-md border border-border bg-surface-high transition-colors duration-150 group-hover:border-border-strong"
                >
                  <ThumbImage :src="clip.thumbnailUrl" class="h-full w-full object-cover" />
                </span>
                <span class="min-w-0">
                  <span
                    class="block truncate text-[11.5px] font-semibold leading-tight text-text-primary transition-colors duration-150 group-hover:text-accent"
                  >
                    {{ clip.title }}
                  </span>
                  <span class="mt-0.5 flex items-center gap-1.5 text-[10px] text-text-secondary">
                    <GameTag v-if="clip.game" :tag="clip.game.tag" />
                    <span class="min-w-0 truncate">
                      <span class="text-accent">@{{ clip.author.username }}</span>
                      · {{ formatNum(clip.viewCount) }} views
                    </span>
                  </span>
                </span>
              </li>
            </ol>

            <!-- Live now — follows currently online (authenticated + non-empty only) -->
            <div v-if="liveFollows.length" class="mt-4 border-t border-border pt-3.5">
              <p
                class="m-0 flex items-center gap-1.5 text-[10px] font-bold uppercase tracking-[0.12em] text-text-muted"
              >
                <span class="size-[7px] rounded-full bg-accent" aria-hidden="true"></span>
                Live now
              </p>
              <div class="mt-2 flex items-center">
                <RouterLink
                  v-for="(u, i) in liveFollows"
                  :key="u.id"
                  :to="{ name: 'user', params: { username: u.username } }"
                  :title="u.username"
                  class="inline-flex rounded-full"
                  :class="i > 0 ? '-ml-2' : ''"
                >
                  <UserAvatar :user="u" :size="28" class="border-2 border-surface-base" />
                </RouterLink>
                <span
                  v-if="liveFollowsOverflow > 0"
                  class="-ml-2 inline-flex size-7 items-center justify-center rounded-full border-2 border-surface-base bg-surface-high text-[10px] font-bold text-text-secondary"
                >
                  +{{ liveFollowsOverflow }}
                </span>
              </div>
            </div>
          </aside>
        </div>
      </section>

      <!-- ====== Top Games ====== -->
      <!-- Hidden under an active game filter: it's global discovery that would
           contradict the focused, single-game view. -->
      <section v-if="bandGames.length && !activeGameSlug" class="mt-8 border-t border-border pt-7">
        <SectionHeader kicker="Browse" title="Top Games" :more-to="{ name: 'games' }" />
        <div class="grid grid-cols-5 gap-3 max-lg:grid-cols-3 max-tablet:grid-cols-2">
          <GameCoverTile v-for="(g, i) in bandGames" :key="g.id" :game="g" :rank="i + 1" />
        </div>
      </section>

      <!-- ====== Trending ====== -->
      <!-- Hidden under an active game filter (global discovery, not this game's feed). -->
      <section v-if="trendingFeature && !activeGameSlug" class="mt-8 border-t border-border pt-7">
        <SectionHeader kicker="Live" title="Trending" :more-to="{ name: 'trending' }" />
        <div class="grid grid-cols-[1fr_280px] items-start gap-5 max-lg:grid-cols-1">
          <!-- Feature -->
          <article
            class="group grid min-w-0 cursor-pointer grid-cols-[240px_1fr] items-start gap-4 rounded-lg border border-border bg-surface-raised p-3.5 transition-colors duration-150 hover:border-border-strong max-tablet:grid-cols-1"
            role="link"
            tabindex="0"
            :aria-label="`Open clip: ${trendingFeature.title}`"
            @click="openClip(trendingFeature.id)"
            @keydown.enter.self="openClip(trendingFeature.id)"
            @keydown.space.self.prevent="openClip(trendingFeature.id)"
          >
            <div
              class="relative aspect-video overflow-hidden rounded-md border border-border bg-surface-high"
            >
              <ThumbImage
                :src="trendingFeature.thumbnailUrl"
                class="h-full w-full object-cover"
              />
              <DurationBadge
                :seconds="trendingFeature.durationSecs"
                class="absolute right-2 bottom-2"
              />
            </div>
            <div class="min-w-0">
              <div v-if="trendingFeature.game" class="mb-2" @click.stop>
                <RouterLink
                  :to="{ name: 'game-detail', params: { slug: trendingFeature.game.slug } }"
                >
                  <GameTag :tag="trendingFeature.game.tag" />
                </RouterLink>
              </div>
              <h3
                class="m-0 line-clamp-2 text-sm font-semibold leading-snug text-text-primary transition-colors duration-150 group-hover:text-accent"
              >
                {{ trendingFeature.title }}
              </h3>
              <p class="m-0 mt-2 text-[11px] text-text-muted">
                <span class="font-medium text-accent">@{{ trendingFeature.author.username }}</span>
                · {{ formatNum(trendingFeature.viewCount) }} views ·
                {{ formatRelativeTime(trendingFeature.createdAt) }}
              </p>
            </div>
          </article>

          <!-- Runner-ups -->
          <ol class="m-0 flex list-none flex-col p-0">
            <li
              v-for="(clip, i) in trendingList"
              :key="clip.id"
              class="group grid cursor-pointer grid-cols-[30px_1fr_auto] items-center gap-2.5 border-b border-border py-2.5 first:pt-0"
              role="link"
              tabindex="0"
              :aria-label="`Open clip: ${clip.title}`"
              @click="openClip(clip.id)"
              @keydown.enter.self="openClip(clip.id)"
              @keydown.space.self.prevent="openClip(clip.id)"
            >
              <span class="font-condensed text-xl font-black leading-none text-text-muted">
                {{ String(i + 2).padStart(2, '0') }}
              </span>
              <span class="min-w-0">
                <span
                  class="block truncate text-[11.5px] font-semibold leading-tight text-text-primary transition-colors duration-150 group-hover:text-accent"
                >
                  {{ clip.title }}
                </span>
                <span class="mt-0.5 flex items-center gap-1.5 text-[10px] text-text-secondary">
                  <GameTag v-if="clip.game" :tag="clip.game.tag" />
                  <span class="min-w-0 truncate text-accent">@{{ clip.author.username }}</span>
                </span>
              </span>
              <span class="shrink-0 text-[11px] font-semibold text-text-secondary">
                {{ formatNum(clip.viewCount) }}
              </span>
            </li>
          </ol>
        </div>
      </section>

      <!-- ====== Recent Clips ====== -->
      <section class="mt-8 border-t border-border pt-7">
        <SectionHeader kicker="New" title="Recent Clips" />
        <div
          v-if="feed.length"
          data-testid="feed-grid"
          class="grid grid-cols-4 gap-3.5 max-lg:grid-cols-2 max-tablet:grid-cols-1"
        >
          <ClipCard v-for="clip in feed" :key="clip.id" :clip="clip" @click="openClip(clip.id)" />
        </div>
        <p v-else class="m-0 text-[11px] text-text-muted">Everything is above the fold today.</p>

        <LoadMoreButton
          v-if="cursor || paginationErrored"
          class="mt-10"
          :loading="loading"
          :errored="paginationErrored"
          @load="loadMore"
        />
      </section>
    </template>

    <ReelsFab />
  </main>
</template>
