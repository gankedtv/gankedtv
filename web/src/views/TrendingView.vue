<script setup lang="ts">
import { computed, ref, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { clips, type ClipFeedItem } from '@/api/clips'
import { games as gamesApi, type GameListItem } from '@/api/games'
import { useLatestRequest } from '@/composables/useLatestRequest'
import { formatNum, formatRelativeTime } from '@/lib/format'
import GameTag from '@/components/GameTag.vue'
import GameCoverTile from '@/components/GameCoverTile.vue'
import DurationBadge from '@/components/DurationBadge.vue'
import SectionHeader from '@/components/SectionHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'

const router = useRouter()

// Only 24h and 7d hit the server-side trending feed today. The other windows
// render as disabled tabs (visible, never emitting) until follow-up windows land.
type TrendingWindow = '24h' | '7d'
const TIME_WINDOWS: { key: string; label: string; disabled?: boolean }[] = [
  { key: '1h', label: 'Last hour', disabled: true },
  { key: '24h', label: '24 hours' },
  { key: '7d', label: 'This week' },
  { key: '30d', label: 'This month', disabled: true },
  { key: 'all', label: 'All time', disabled: true },
]

const timeWindow = ref<TrendingWindow>('24h')

// Hot games don't depend on the window, so reloading them on each toggle is wasted work
// but the simplicity wins (and they're cheap). One composable covers both so a rapid
// window flip still gets one shared loading/errored state.
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

// Feature band — #1 hero + four runner-ups; Full Chart — everything after.
const feature = computed(() => topClips.value[0] ?? null)
const runnerUps = computed(() => topClips.value.slice(1, 5))
const longTail = computed(() => topClips.value.slice(5, 25))

onMounted(run)
watch(timeWindow, run)

function selectWindow(key: string) {
  // Disabled tabs never emit, so only supported windows reach here.
  if (key === timeWindow.value) return
  timeWindow.value = key as TrendingWindow
}

function openClip(id: string) {
  router.push({ name: 'clip', params: { id } })
}
</script>

<template>
  <main class="mx-auto max-w-300 px-7 pt-7 pb-16 max-tablet:px-4">
    <PageHeader title="Trending">
      <template #caption>Likes × 3 + views, decayed by age</template>

      <p class="sr-only">
        24-hour and 7-day windows are available; the other ranges are coming soon.
      </p>
      <UnderlineTabs
        class="mt-5"
        :tabs="TIME_WINDOWS"
        :active="timeWindow"
        @select="selectWindow"
      />
    </PageHeader>

    <StatusPanel v-if="errored" kind="error" message="Couldn't load trending.">
      <button
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
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
      <!-- ====== Feature band — #1 hero + runner-ups ====== -->
      <section class="mt-7">
        <div class="grid grid-cols-[1.6fr_1fr] items-start gap-x-6 gap-y-8 max-lg:grid-cols-1">
          <!-- Feature -->
          <article
            class="group flex min-w-0 cursor-pointer flex-col gap-4"
            @click="openClip(feature.id)"
          >
            <div class="flex items-end gap-4">
              <span
                class="font-condensed text-[clamp(40px,5vw,64px)] font-black leading-[0.92] text-accent"
                aria-hidden="true"
              >
                1
              </span>
              <h2
                class="m-0 pb-1 font-condensed text-[clamp(22px,2.6vw,34px)] font-black uppercase leading-[1.02] text-text-primary transition-colors duration-150 group-hover:text-accent"
              >
                {{ feature.title }}
              </h2>
            </div>
            <div
              class="relative aspect-video overflow-hidden rounded-lg border border-border bg-black transition-colors duration-150 group-hover:border-border-strong"
            >
              <img :src="feature.thumbnailUrl" alt="" class="h-full w-full object-cover" />
              <div v-if="feature.game" class="absolute bottom-2 left-2">
                <GameTag :tag="feature.game.tag" />
              </div>
              <DurationBadge :seconds="feature.durationSecs" class="absolute bottom-2 right-2" />
            </div>
            <p class="m-0 text-[11px] text-text-muted">
              <span class="font-medium text-accent">@{{ feature.author.username }}</span>
              · ♥ {{ formatNum(feature.likeCount) }} · {{ formatNum(feature.viewCount) }} views ·
              {{ formatRelativeTime(feature.createdAt) }}
            </p>
          </article>

          <!-- Runner-ups -->
          <ol class="m-0 flex list-none flex-col p-0">
            <li
              v-for="(clip, i) in runnerUps"
              :key="clip.id"
              class="group flex cursor-pointer items-center gap-3 border-t border-border py-3 first:border-t-0 first:pt-0"
              @click="openClip(clip.id)"
            >
              <span
                class="min-w-8 font-condensed text-xl font-black leading-none text-text-muted"
              >
                {{ String(i + 2).padStart(2, '0') }}
              </span>
              <span
                class="relative aspect-video w-28 shrink-0 overflow-hidden rounded-md border border-border bg-black transition-colors duration-150 group-hover:border-border-strong"
              >
                <img :src="clip.thumbnailUrl" alt="" class="h-full w-full object-cover" />
              </span>
              <span class="min-w-0 flex-1">
                <span
                  class="block truncate text-[11.5px] font-semibold leading-tight text-text-primary transition-colors duration-150 group-hover:text-accent"
                >
                  {{ clip.title }}
                </span>
                <span class="mt-0.5 block truncate text-[10px] text-text-secondary">
                  <span class="text-accent">@{{ clip.author.username }}</span>
                  · ♥ {{ formatNum(clip.likeCount) }}
                </span>
              </span>
            </li>
          </ol>
        </div>
      </section>

      <!-- ====== Hot Games ====== -->
      <section v-if="hotGames.length" class="mt-8 border-t border-border pt-7">
        <SectionHeader kicker="Browse" title="Hot Games" :more-to="{ name: 'games' }" />
        <div class="flex gap-3 overflow-x-auto pb-1">
          <div v-for="g in hotGames" :key="g.id" class="w-30 shrink-0">
            <GameCoverTile :game="g" />
          </div>
        </div>
      </section>

      <!-- ====== Full Chart ====== -->
      <section v-if="longTail.length" class="mt-8 border-t border-border pt-7">
        <SectionHeader kicker="Ranked" title="Full Chart" />
        <ol class="m-0 flex list-none flex-col p-0">
          <li
            v-for="(clip, i) in longTail"
            :key="clip.id"
            class="group grid cursor-pointer grid-cols-[36px_120px_1fr_auto] items-center gap-3 border-b border-border py-2.5 last:border-b-0 max-tablet:grid-cols-[32px_88px_1fr]"
            @click="openClip(clip.id)"
          >
            <span
              class="font-condensed text-[22px] font-black leading-none text-text-muted"
              aria-hidden="true"
            >
              {{ String(i + 6).padStart(2, '0') }}
            </span>
            <span
              class="relative block aspect-video overflow-hidden rounded-md border border-border bg-black transition-colors duration-150 group-hover:border-border-strong"
            >
              <img :src="clip.thumbnailUrl" alt="" class="h-full w-full object-cover" />
            </span>
            <span class="min-w-0">
              <span
                class="block truncate text-sm font-semibold leading-tight text-text-primary transition-colors duration-150 group-hover:text-accent"
              >
                {{ clip.title }}
              </span>
              <span class="mt-0.5 block truncate text-[11px] text-text-muted">
                <span class="font-medium text-accent">@{{ clip.author.username }}</span>
                · ♥ {{ formatNum(clip.likeCount) }} · {{ formatRelativeTime(clip.createdAt) }}
              </span>
            </span>
            <span class="shrink-0 text-[11px] font-semibold text-text-secondary max-tablet:hidden">
              {{ formatNum(clip.viewCount) }} views
            </span>
          </li>
        </ol>
      </section>
    </template>
  </main>
</template>
