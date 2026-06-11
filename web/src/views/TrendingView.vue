<script setup lang="ts">
import { computed, ref, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { clips, type ClipFeedItem } from '@/api/clips'
import { games as gamesApi, type GameListItem } from '@/api/games'
import { useLatestRequest } from '@/composables/useLatestRequest'
import { formatNum, formatRelativeTime } from '@/lib/format'
import { issueNumber } from '@/lib/issue'
import ClipCard from '@/components/ClipCard.vue'
import GameTag from '@/components/GameTag.vue'
import DurationBadge from '@/components/DurationBadge.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'

const router = useRouter()

// Only 24h and 7d hit the server-side trending feed today. The other windows surface the
// UX intent (and the issue's "drop fake arrows for v1" sentiment is to ship what the
// server actually supports), so they're rendered but aria-disabled until follow-up windows land.
type TrendingWindow = '24h' | '7d'
const TIME_WINDOWS = [
  { key: '1h', label: 'Last hour', enabled: false },
  { key: '24h', label: '24 hours', enabled: true },
  { key: '7d', label: 'This week', enabled: true },
  { key: '30d', label: 'This month', enabled: false },
  { key: 'all', label: 'All time', enabled: false },
] as const

const timeWindow = ref<TrendingWindow>('24h')

// Hot games don't depend on the window, so reloading them on each toggle is wasted work
// but the simplicity wins (and they're cheap). One composable covers both so a rapid
// window flip still gets a single loading/errored signal.
const { data, loading, errored, run } = useLatestRequest<{
  topClips: ClipFeedItem[]
  hotGames: GameListItem[]
}>(
  async () => {
    const [feed, games] = await Promise.all([
      clips.feed({ sort: 'trending', window: timeWindow.value, limit: 50 }),
      gamesApi.list(8),
    ])
    return { topClips: feed.items.slice(0, 50), hotGames: games }
  },
  { label: 'trending' },
)

const topClips = computed(() => data.value?.topClips ?? [])
const hotGames = computed(() => data.value?.hotGames ?? [])

// Band I — feature + four runner-ups; Band II — everything after.
const feature = computed(() => topClips.value[0] ?? null)
const runnerUps = computed(() => topClips.value.slice(1, 5))
const longTail = computed(() => topClips.value.slice(5, 25))

onMounted(run)
watch(timeWindow, run)

function selectWindow(key: string, enabled: boolean) {
  if (!enabled || key === timeWindow.value) return
  timeWindow.value = key as TrendingWindow
}

function openClip(id: string) {
  router.push({ name: 'clip', params: { id } })
}

// Underline-tab recipe, inline because UnderlineTabs has no disabled state.
const tabBase =
  '-mb-px cursor-pointer whitespace-nowrap border-b-2 bg-transparent pb-3 font-mono text-[11px] uppercase tracking-[0.15em] transition-colors duration-150'
const tabActive = `${tabBase} border-ink text-text-primary`
const tabEnabled = `${tabBase} border-transparent text-text-secondary hover:text-ink`
const tabDisabled = `${tabBase.replace('cursor-pointer', 'cursor-not-allowed')} border-transparent text-text-muted opacity-60`
</script>

<template>
  <main class="mx-auto max-w-360 px-8 pt-10 pb-30 max-tablet:px-4 max-tablet:pt-5">
    <PageHeader title="Trending">
      <template #caption>
        <span class="text-ink">The Chart</span>&nbsp;· likes × 3 + views, decayed by age
      </template>

      <p id="time-window-hint" class="sr-only">
        24-hour and 7-day windows are available; the other ranges are coming soon.
      </p>
      <div
        class="mt-6 flex gap-7 border-b border-border"
        role="group"
        aria-label="Trending time window"
      >
        <button
          v-for="tw in TIME_WINDOWS"
          :key="tw.key"
          type="button"
          :class="timeWindow === tw.key ? tabActive : tw.enabled ? tabEnabled : tabDisabled"
          :aria-disabled="!tw.enabled"
          :aria-pressed="tw.key === timeWindow"
          aria-describedby="time-window-hint"
          @click="selectWindow(tw.key, tw.enabled)"
        >
          {{ tw.label }}
        </button>
      </div>
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load trending.">
      <button
        class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
        @click="run"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel v-else-if="loading && topClips.length === 0" kind="loading" message="Loading" />

    <StatusPanel
      v-else-if="!loading && topClips.length === 0"
      kind="empty"
      message="No clips trending yet — check back soon."
    />

    <template v-else-if="feature">
      <!-- ====== Band I — Top Mover ====== -->
      <section class="pt-8">
        <SectionHeader roman="I" kicker="Top Mover" blurb="What climbed the chart overnight." />
        <div class="grid grid-cols-[1.6fr_1fr] items-start gap-x-9 gap-y-8 pt-6 max-lg:grid-cols-1">
          <!-- Feature -->
          <article
            class="group flex min-w-0 cursor-pointer flex-col gap-4"
            @click="openClip(feature.id)"
          >
            <div class="flex items-end gap-5">
              <span
                class="font-heading text-[clamp(48px,6vw,72px)] font-bold leading-[0.92] tracking-[-0.01em] text-ink"
                aria-hidden="true"
              >
                1
              </span>
              <h2
                class="m-0 pb-1 font-heading text-[clamp(24px,3vw,38px)] font-bold uppercase leading-[1.02] text-text-primary transition-colors duration-150 group-hover:text-ink"
              >
                {{ feature.title }}
              </h2>
            </div>
            <div
              class="relative aspect-video overflow-hidden border border-border bg-surface-sunken transition-colors duration-150 group-hover:border-ink"
            >
              <img :src="feature.thumbnailUrl" alt="" class="h-full w-full object-cover" />
              <span
                class="absolute left-2.5 top-2 font-heading text-2xl font-bold leading-none text-ink opacity-85"
                aria-hidden="true"
              >
                No. {{ issueNumber(feature.id) }}
              </span>
              <div v-if="feature.game" class="absolute bottom-2 left-2">
                <GameTag :tag="feature.game.tag" />
              </div>
              <DurationBadge :seconds="feature.durationSecs" class="absolute bottom-2 right-2" />
            </div>
            <p class="m-0 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted">
              <span class="text-ink">@{{ feature.author.username }}</span>
              · ♥ {{ formatNum(feature.likeCount) }} · {{ formatNum(feature.viewCount) }} views ·
              {{ formatRelativeTime(feature.createdAt) }}
            </p>
          </article>

          <!-- Runner-ups -->
          <ol class="m-0 flex list-none flex-col p-0">
            <li
              v-for="(clip, i) in runnerUps"
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
                  @{{ clip.author.username }} · ♥ {{ formatNum(clip.likeCount) }}
                </span>
              </span>
            </li>
          </ol>
        </div>
      </section>

      <!-- ====== Band II — The Long Tail ====== -->
      <section v-if="longTail.length" class="pt-10">
        <SectionHeader roman="II" kicker="The Long Tail" />
        <div class="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-x-5.5 gap-y-7 pt-6">
          <ClipCard
            v-for="clip in longTail"
            :key="clip.id"
            :clip="clip"
            @click="openClip(clip.id)"
          />
        </div>
      </section>

      <!-- ====== Band III — Hot Games ====== -->
      <section v-if="hotGames.length" class="pt-10">
        <SectionHeader roman="III" kicker="Hot Games" :more-to="{ name: 'games' }" />
        <div class="flex flex-wrap gap-x-8 gap-y-3 pt-6">
          <RouterLink
            v-for="g in hotGames"
            :key="g.id"
            :to="{ name: 'game-detail', params: { slug: g.slug } }"
            :aria-label="`Browse ${g.name} clips`"
            class="group flex items-center gap-3 outline-none"
          >
            <GameTag :tag="g.tag" variant="square" />
            <span class="flex min-w-0 flex-col gap-px">
              <span
                class="truncate font-heading text-[15px] font-medium uppercase text-text-primary transition-colors duration-150 group-hover:text-ink"
              >
                {{ g.name }}
              </span>
              <span class="font-mono text-[10px] text-text-muted">{{ g.slug }}</span>
            </span>
          </RouterLink>
        </div>
      </section>
    </template>
  </main>
</template>
