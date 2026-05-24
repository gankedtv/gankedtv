<script setup lang="ts">
import { computed } from 'vue'
import type { LeaderboardEntry } from '@/api/leaderboards'
import { formatNum } from '@/lib/format'
import GameTag from '@/components/GameTag.vue'
import DurationBadge from '@/components/DurationBadge.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import IconChevronRight from '@/components/icons/IconChevronRight.vue'

const props = defineProps<{
  entry: LeaderboardEntry
  // When true, surfaces the entry's own `windowLikes` (counted within the window)
  // instead of the clip's all-time `likeCount`. The standalone /leaderboards page
  // wants the windowed number — that's what makes the ranking meaningful — while
  // a global "popular this week" embed can opt back into the all-time count.
  showWindowLikes?: boolean
}>()

const isTopThree = computed(() => props.entry.rank <= 3)
const displayedLikes = computed(() =>
  props.showWindowLikes === false ? props.entry.clip.likeCount : props.entry.windowLikes,
)
</script>

<template>
  <RouterLink
    :to="{ name: 'clip', params: { id: entry.clip.id } }"
    :aria-label="`#${entry.rank}: ${entry.clip.title}`"
    class="grid grid-cols-[40px_120px_1fr_auto_auto] items-center gap-4 border-b border-border px-4 py-3 outline-none transition-[background] duration-150 last:border-b-0 hover:bg-surface-overlay focus-visible:bg-surface-overlay focus-visible:ring-2 focus-visible:ring-brand-light"
  >
    <span
      class="font-heading text-[28px] leading-none font-bold"
      :class="isTopThree ? 'text-brand-light' : 'text-text-muted'"
      >#{{ entry.rank }}</span
    >

    <div class="relative aspect-video overflow-hidden rounded-[4px] bg-surface-sunken">
      <img :src="entry.clip.thumbnailUrl" alt="" class="block h-full w-full object-cover" />
      <DurationBadge :seconds="entry.clip.durationSecs" class="absolute right-1 bottom-1" />
    </div>

    <div class="flex min-w-0 flex-col gap-1">
      <span
        class="line-clamp-2 font-body text-[13px] leading-[1.35] font-medium text-text-primary"
        >{{ entry.clip.title }}</span
      >
      <div class="flex items-center gap-1.5 font-mono text-[10px]">
        <GameTag v-if="entry.clip.game" :tag="entry.clip.game.tag" tone="subtle" />
        <AuthorHandle :username="entry.clip.author.username" class="text-neon" />
      </div>
    </div>

    <div
      class="flex flex-col gap-1 text-right font-mono text-[11px] whitespace-nowrap text-text-secondary"
    >
      <span>♥ {{ formatNum(displayedLikes) }}</span>
      <span class="text-text-muted">{{ formatNum(entry.clip.viewCount) }} plays</span>
    </div>

    <div class="text-text-muted">
      <IconChevronRight :size="16" />
    </div>
  </RouterLink>
</template>
