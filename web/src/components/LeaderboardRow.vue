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
}>()

const isTopThree = computed(() => props.entry.rank <= 3)
</script>

<template>
  <RouterLink
    :to="{ name: 'clip', params: { id: entry.clip.id } }"
    :aria-label="`#${entry.rank}: ${entry.clip.title}`"
    class="group grid grid-cols-[40px_120px_1fr_auto_auto] items-center gap-4 border-b border-border px-1 py-3.5 outline-none last:border-b-0 focus-visible:bg-surface-raised max-tablet:grid-cols-[32px_88px_1fr_auto]"
  >
    <span
      class="font-heading text-[28px] leading-none font-bold transition-colors duration-150 group-hover:text-ink"
      :class="isTopThree ? 'text-ink' : 'text-text-muted'"
      >{{ entry.rank }}</span
    >

    <div
      class="relative aspect-video overflow-hidden border border-border bg-surface-sunken transition-colors duration-150 group-hover:border-ink"
    >
      <img :src="entry.clip.thumbnailUrl" alt="" class="block h-full w-full object-cover" />
      <DurationBadge :seconds="entry.clip.durationSecs" class="absolute right-1 bottom-1" />
    </div>

    <div class="flex min-w-0 flex-col gap-1">
      <span
        class="line-clamp-2 font-heading text-[15px] font-medium uppercase leading-[1.15] text-text-primary transition-colors duration-150 group-hover:text-ink"
        >{{ entry.clip.title }}</span
      >
      <div class="flex items-center gap-1.5 font-mono text-[10px]">
        <GameTag v-if="entry.clip.game" :tag="entry.clip.game.tag" tone="subtle" />
        <AuthorHandle :username="entry.clip.author.username" class="text-ink" />
      </div>
    </div>

    <div
      class="flex flex-col gap-1 text-right font-mono text-[10px] uppercase tracking-[0.06em] whitespace-nowrap text-text-secondary"
    >
      <span class="font-heading text-lg font-bold normal-case tracking-normal text-text-primary">
        ♥ {{ formatNum(entry.windowLikes) }}
      </span>
      <span class="text-text-muted">{{ formatNum(entry.clip.viewCount) }} plays</span>
    </div>

    <div class="text-text-muted max-tablet:hidden">
      <IconChevronRight :size="16" />
    </div>
  </RouterLink>
</template>
