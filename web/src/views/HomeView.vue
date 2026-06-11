<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { clips, type ClipFeedItem } from '@/api/clips'
import { games as gamesApi, type GameListItem } from '@/api/games'
import { useAuthStore } from '@/stores/auth'
import { formatNum, formatDuration, formatRelativeTime } from '@/lib/format'
import { issueNumber } from '@/lib/issue'
import ClipCard from '@/components/ClipCard.vue'
import GameTag from '@/components/GameTag.vue'
import TagChip from '@/components/TagChip.vue'
import DurationBadge from '@/components/DurationBadge.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import GameCoverTile from '@/components/GameCoverTile.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'
import LoadMoreButton from '@/components/LoadMoreButton.vue'
import ReelsFab from '@/components/reels/ReelsFab.vue'
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
// Daily "Clip of the Day" pick. Loaded once on mount in parallel with the feed.
// Survives tab switches because it's a global pick, not per-source.
const featured = ref<ClipFeedItem | null>(null)
// Initial-load failure (no items rendered yet) — full-page error panel.
const errored = ref(false)
// Pagination failure with content already on screen — inline retry on the
// "Load more" button so we don't blow away the loaded feed.
const paginationErrored = ref(false)

// Side-loads for the By Game / Trending bands. Silent failure — the band simply
// doesn't render, the feed is never blocked on them.
const bandGames = ref<GameListItem[]>([])
const bandTrending = ref<ClipFeedItem[]>([])

// Hero prefers today's featured pick (computed server-side via /clips/featured)
// and falls back to items[0] (newest ready clip) so the hero never goes blank
// — handles fresh-platform / pre-engagement / featured-fetch-failed cases.
const hero = computed<ClipFeedItem | null>(() => featured.value ?? items.value[0] ?? null)
// True only when the hero is actually today's featured pick — the badge must
// not lie about provenance.
const heroIsFeatured = computed(() => featured.value !== null)
// The hero isn't always items[0] — a featured "Clip of the Day" sits outside the
// feed ordering. Slicing the bands from a fixed offset would drop the newest clip
// and double-render the featured pick, so exclude the hero clip by id instead.
const rest = computed(() => {
  const heroId = hero.value?.id
  return heroId ? items.value.filter((c) => c.id !== heroId) : items.value
})
// Band I right rail — ranked Latest Drops list.
const latestDrops = computed(() => rest.value.slice(0, 5))
// Band IV — everything else, freshest first.
const feed = computed(() => rest.value.slice(5))

// Band III — feature + four runner-ups.
const trendingFeature = computed(() => bandTrending.value[0] ?? null)
const trendingList = computed(() => bandTrending.value.slice(1, 5))

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
  // Independent loads — no band failure may block the feed and vice versa.
  void Promise.allSettled([loadMore(), loadFeatured(), loadBandGames(), loadBandTrending()])
})
</script>

<template>
  <main class="mx-auto max-w-360 px-8 pt-10 pb-30 max-tablet:px-4 max-tablet:pt-5 max-tablet:pb-20">
    <!-- Initial loading state — explicit so the empty-state branch doesn't flash
         in the gap between mount and the first response. -->
    <StatusPanel
      v-if="loading && items.length === 0 && !errored"
      kind="loading"
      message="Loading"
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
          class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
          @click="selectTab('public')"
        >
          Browse Latest
        </button>
        <RouterLink
          to="/games"
          class="border border-border px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
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
        class="border border-border px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
      >
        Upload a clip
      </RouterLink>
    </StatusPanel>

    <!-- Error state -->
    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load the feed.">
      <button
        class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
        @click="loadMore"
      >
        Retry
      </button>
    </StatusPanel>

    <template v-else-if="hero">
      <!-- ====== Band I — Hero + Latest Drops ====== -->
      <section>
        <SectionHeader
          roman="I"
          :kicker="heroIsFeatured ? 'Clip of the Day' : 'Featured Clip'"
        />
        <div class="grid grid-cols-[2fr_1fr] gap-x-9 gap-y-8 pt-6 max-lg:grid-cols-1">
          <!-- Hero -->
          <article class="group flex min-w-0 cursor-pointer flex-col" @click="openClip(hero.id)">
            <div class="flex items-end gap-5">
              <span
                class="font-heading text-[clamp(56px,8vw,96px)] font-bold leading-[0.92] tracking-[-0.01em] text-ink"
                aria-hidden="true"
              >
                {{ issueNumber(hero.id) }}
              </span>
              <h2
                class="m-0 pb-1.5 font-heading text-[clamp(28px,3.5vw,44px)] font-bold uppercase leading-[1.02] text-text-primary transition-colors duration-150 group-hover:text-ink"
              >
                {{ hero.title }}
              </h2>
            </div>
            <div
              class="relative mt-5 aspect-video overflow-hidden border border-border bg-surface-sunken transition-colors duration-150 group-hover:border-ink"
            >
              <img :src="hero.thumbnailUrl" alt="" class="h-full w-full object-cover" />
              <div v-if="hero.game" class="absolute bottom-2.5 left-2.5">
                <GameTag :tag="hero.game.tag" size="md" />
              </div>
              <DurationBadge
                :seconds="hero.durationSecs"
                size="md"
                class="absolute bottom-2.5 right-2.5"
              />
              <button
                class="absolute inset-0 flex cursor-pointer items-center justify-center bg-transparent"
                :aria-label="`Play: ${hero.title}`"
                @click.stop="openClip(hero.id)"
              >
                <span
                  class="inline-flex size-18 items-center justify-center border border-[#f4f1e8]/25 bg-black/45 text-[#f4f1e8] transition-colors duration-150 group-hover:border-ink group-hover:bg-ink group-hover:text-signal-text"
                >
                  <IconPlay :size="26" />
                </span>
              </button>
            </div>
            <div
              class="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1.5 font-mono text-[11px] uppercase tracking-[0.1em] text-text-muted"
            >
              <AuthorHandle
                :username="hero.author.username"
                as="link"
                class="text-ink"
                @click.stop
              />
              <span>· {{ formatRelativeTime(hero.createdAt) }}</span>
              <span v-if="hero.game">· {{ hero.game.name }}</span>
              <span class="ml-auto flex items-center gap-3">
                <span>{{ formatNum(hero.viewCount) }} views</span>
                <span>{{ formatNum(hero.likeCount) }} ♥</span>
                <span v-if="hero.durationSecs !== null">{{
                  formatDuration(hero.durationSecs)
                }}</span>
              </span>
            </div>
            <div v-if="hero.tags.length" class="mt-3 flex flex-wrap gap-2" @click.stop>
              <TagChip v-for="t in hero.tags" :key="t.id" :slug="t.slug" :name="t.name" size="md" />
            </div>
          </article>

          <!-- Latest Drops — ranked list -->
          <aside v-if="latestDrops.length" class="min-w-0">
            <p
              class="m-0 font-mono text-[10px] font-medium uppercase leading-none tracking-[0.22em] text-text-secondary"
            >
              Latest Drops
            </p>
            <ol data-testid="latest-drops" class="m-0 mt-2 flex list-none flex-col p-0">
              <li
                v-for="(clip, i) in latestDrops"
                :key="clip.id"
                class="group flex cursor-pointer items-center gap-4 border-t border-border py-3.5 first:border-t-0"
                @click="openClip(clip.id)"
              >
                <span
                  class="min-w-10 font-heading text-[28px] font-bold leading-none text-text-muted transition-colors duration-150 group-hover:text-ink"
                >
                  {{ String(i + 1).padStart(2, '0') }}
                </span>
                <span class="min-w-0 flex-1">
                  <span
                    class="block truncate font-heading text-[17px] font-medium uppercase leading-[1.1] text-text-primary transition-colors duration-150 group-hover:text-ink"
                  >
                    {{ clip.title }}
                  </span>
                  <span
                    class="mt-1 block font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
                  >
                    @{{ clip.author.username }} · {{ formatRelativeTime(clip.createdAt) }}
                  </span>
                </span>
                <span class="shrink-0 font-heading text-xl font-bold text-text-primary">
                  {{ formatNum(clip.viewCount) }}
                </span>
              </li>
            </ol>
          </aside>
        </div>
      </section>

      <!-- ====== Band II — By Game ====== -->
      <section v-if="bandGames.length" class="pt-10">
        <SectionHeader
          roman="II"
          kicker="By Game"
          title="The Catalogue"
          blurb="Where the bracket lives this week."
          :more-to="{ name: 'games' }"
        />
        <div class="grid grid-cols-5 gap-x-5.5 gap-y-7 pt-6 max-lg:grid-cols-3 max-tablet:grid-cols-2">
          <GameCoverTile v-for="g in bandGames" :key="g.id" :game="g" />
        </div>
      </section>

      <!-- ====== Band III — Trending 24h ====== -->
      <section v-if="trendingFeature" class="pt-10">
        <SectionHeader
          roman="III"
          kicker="Trending · 24h"
          title="What Climbed Overnight"
          :more-to="{ name: 'trending' }"
        />
        <div class="grid grid-cols-[1.6fr_1fr] gap-x-9 gap-y-8 pt-6 max-lg:grid-cols-1">
          <!-- Feature -->
          <article
            class="group flex min-w-0 cursor-pointer flex-col gap-4"
            @click="openClip(trendingFeature.id)"
          >
            <div
              class="relative aspect-video overflow-hidden border border-border bg-surface-sunken transition-colors duration-150 group-hover:border-ink"
            >
              <img :src="trendingFeature.thumbnailUrl" alt="" class="h-full w-full object-cover" />
              <span
                class="absolute left-2.5 top-2 font-heading text-2xl font-bold leading-none text-ink opacity-85"
                aria-hidden="true"
              >
                No. {{ issueNumber(trendingFeature.id) }}
              </span>
              <div v-if="trendingFeature.game" class="absolute bottom-2 left-2">
                <GameTag :tag="trendingFeature.game.tag" />
              </div>
              <DurationBadge
                :seconds="trendingFeature.durationSecs"
                class="absolute bottom-2 right-2"
              />
            </div>
            <div>
              <h3
                class="m-0 font-heading text-[26px] font-bold uppercase leading-[1.05] text-text-primary transition-colors duration-150 group-hover:text-ink"
              >
                {{ trendingFeature.title }}
              </h3>
              <p
                class="m-0 mt-2 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
              >
                <span class="text-ink">@{{ trendingFeature.author.username }}</span>
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
              class="group flex cursor-pointer items-center gap-4 border-t border-border py-3.5 first:border-t-0 first:pt-0"
              @click="openClip(clip.id)"
            >
              <span
                class="min-w-8 font-heading text-[28px] font-bold leading-none text-text-muted transition-colors duration-150 group-hover:text-ink"
              >
                {{ i + 2 }}
              </span>
              <span
                class="relative aspect-video w-28 shrink-0 overflow-hidden border border-border bg-surface-sunken transition-colors duration-150 group-hover:border-ink"
              >
                <img :src="clip.thumbnailUrl" alt="" class="h-full w-full object-cover" />
              </span>
              <span class="min-w-0 flex-1">
                <span
                  class="block truncate font-heading text-base font-medium uppercase leading-[1.1] text-text-primary transition-colors duration-150 group-hover:text-ink"
                >
                  {{ clip.title }}
                </span>
                <span
                  class="mt-1 block font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
                >
                  @{{ clip.author.username }} · {{ formatNum(clip.viewCount) }} views
                </span>
              </span>
            </li>
          </ol>
        </div>
      </section>

      <!-- ====== Band IV — The Feed ====== -->
      <section class="pt-10">
        <SectionHeader
          roman="IV"
          kicker="The Feed"
          title="Everything Else"
          blurb="Freshest first."
        >
          <template #right>
            <UnderlineTabs
              class="border-b-0"
              :tabs="TABS"
              :active="source"
              @select="selectTab"
            />
          </template>
        </SectionHeader>
        <div
          v-if="feed.length"
          data-testid="feed-grid"
          class="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-x-5.5 gap-y-7 pt-6"
        >
          <ClipCard v-for="clip in feed" :key="clip.id" :clip="clip" @click="openClip(clip.id)" />
        </div>
        <p
          v-else
          class="m-0 pt-6 font-mono text-[11px] uppercase tracking-widest text-text-muted"
        >
          The whole issue is above the fold today.
        </p>

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
