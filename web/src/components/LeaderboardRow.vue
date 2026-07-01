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

const isFirst = computed(() => props.entry.rank === 1)
</script>

<template>
  <RouterLink
    :to="{ name: 'clip', params: { id: entry.clip.id } }"
    :aria-label="`#${entry.rank}: ${entry.clip.title}`"
    class="group grid grid-cols-[40px_120px_1fr_auto_auto] items-center gap-3 border-b border-border px-1 py-2.5 outline-none last:border-b-0 focus-visible:bg-surface-high max-tablet:grid-cols-[32px_88px_1fr_auto]"
  >
    <span
      class="font-condensed text-[22px] leading-none font-black"
      :class="isFirst ? 'text-accent' : 'text-text-muted'"
      >{{ entry.rank }}</span
    >

    <div
      class="relative aspect-video overflow-hidden rounded-md border border-border bg-black transition-colors duration-150 group-hover:border-border-strong"
    >
      <img :src="entry.clip.thumbnailUrl" alt="" class="block h-full w-full object-cover" />
      <DurationBadge :seconds="entry.clip.durationSecs" class="absolute right-1 bottom-1" />
    </div>

    <div class="flex min-w-0 flex-col gap-1">
      <span
        class="line-clamp-2 font-condensed text-[15px] font-bold uppercase leading-[1.15] text-text-primary transition-colors duration-150 group-hover:text-accent"
        >{{ entry.clip.title }}</span
      >
      <div class="flex items-center gap-1.5 text-[10px]">
        <GameTag v-if="entry.clip.game" :tag="entry.clip.game.tag" tone="subtle" />
        <AuthorHandle
          :username="entry.clip.author.username"
          class="text-xs font-semibold text-accent"
        />
      </div>
    </div>

    <div class="flex flex-col gap-1 text-right whitespace-nowrap">
      <span class="font-condensed text-lg font-bold text-text-primary">
        ♥ {{ formatNum(entry.windowLikes) }}
      </span>
      <span class="text-[10px] uppercase tracking-widest text-text-muted"
        >{{ formatNum(entry.clip.viewCount) }} plays</span
      >
    </div>

    <div class="text-text-muted max-tablet:hidden">
      <IconChevronRight :size="16" />
    </div>
  </RouterLink>
</template>
