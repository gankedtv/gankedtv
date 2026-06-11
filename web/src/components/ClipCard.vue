<script setup lang="ts">
import { computed } from 'vue'
import type { ClipFeedItem } from '@/api/clips'
import { formatNum, formatRelativeTime } from '@/lib/format'
import { issueNumber } from '@/lib/issue'
import GameTag from './GameTag.vue'
import DurationBadge from './DurationBadge.vue'
import AuthorHandle from './AuthorHandle.vue'
import TagChip from './TagChip.vue'
import IconHeart from './icons/IconHeart.vue'
import IconEye from './icons/IconEye.vue'

const MAX_VISIBLE_TAGS = 3

const props = withDefaults(defineProps<{ clip: ClipFeedItem; showAuthor?: boolean }>(), {
  showAuthor: true,
})
const emit = defineEmits<{ click: [] }>()

const visibleTags = computed(() => props.clip.tags.slice(0, MAX_VISIBLE_TAGS))
const overflowCount = computed(() => Math.max(0, props.clip.tags.length - MAX_VISIBLE_TAGS))

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Enter' || e.key === ' ') {
    e.preventDefault()
    emit('click')
  }
}

// Enter fires the link's native synthetic click; Space we preventDefault so it
// doesn't scroll the page on top of the navigation. Both stop so the parent
// article's @keydown doesn't also route to the clip detail. One handler — two
// @keydown bindings would compile to duplicate object keys (TS1117).
function onLinkKeydown(e: KeyboardEvent) {
  if (e.key !== 'Enter' && e.key !== ' ') return
  if (e.key === ' ') e.preventDefault()
  e.stopPropagation()
}
</script>

<template>
  <!-- No card chrome: the clip sits directly on the page surface. Hover is a
       border + title color swap — never a transform, shadow, or glow. -->
  <article
    role="button"
    tabindex="0"
    :aria-label="clip.title"
    class="group flex cursor-pointer flex-col gap-2.5 outline-none"
    @click="emit('click')"
    @keydown="onKeydown"
  >
    <!-- Thumbnail -->
    <div
      class="relative aspect-video overflow-hidden border border-border bg-surface-sunken transition-colors duration-150 group-hover:border-ink group-focus-visible:border-ink"
    >
      <img :src="props.clip.thumbnailUrl" alt="" class="h-full w-full object-cover" />
      <!-- Issue number — the motif is the brand. -->
      <span
        class="absolute left-2.5 top-2 font-heading text-2xl font-bold leading-none text-ink opacity-85"
        aria-hidden="true"
      >
        No. {{ issueNumber(props.clip.id) }}
      </span>
      <!-- Game tag — bottom-left. Links to /game/:slug. The handlers stop both
           pointer and keyboard activation from bubbling to the parent article,
           whose @click + @keydown otherwise route to the clip detail instead. -->
      <RouterLink
        v-if="props.clip.game"
        :to="{ name: 'game-detail', params: { slug: props.clip.game.slug } }"
        :aria-label="`Browse ${props.clip.game.name} clips`"
        class="absolute bottom-2 left-2 outline-none focus-visible:ring-2 focus-visible:ring-ink"
        @click.stop
        @keydown="onLinkKeydown"
      >
        <GameTag :tag="props.clip.game.tag" />
      </RouterLink>
      <!-- Duration — bottom-right -->
      <DurationBadge :seconds="props.clip.durationSecs" class="absolute bottom-2 right-2" />
    </div>

    <!-- Title -->
    <h3
      class="m-0 line-clamp-2 min-h-9 font-heading text-base font-medium uppercase leading-[1.1] text-text-primary transition-colors duration-150 group-hover:text-ink"
    >
      {{ clip.title }}
    </h3>

    <!-- Tag row: up to 3 chips + "+N" overflow indicator. Hidden when the
         clip has no tags so cards without tags don't grow a blank row. Both
         elements .stop the click so the chip area doesn't double as a
         card-click target. -->
    <div v-if="clip.tags.length" class="flex flex-wrap gap-1.5">
      <TagChip v-for="t in visibleTags" :key="t.id" :slug="t.slug" :name="t.name" @click.stop />
      <span
        v-if="overflowCount > 0"
        class="border border-border px-1.5 py-0.5 font-mono text-[10px] font-medium uppercase tracking-[0.08em] text-text-muted"
        @click.stop
      >
        +{{ overflowCount }}
      </span>
    </div>

    <!-- Meta strip — mono caps, @author in ink, stats right-aligned -->
    <div
      class="flex items-center gap-2 overflow-hidden font-mono text-[10px] uppercase tracking-[0.06em] text-text-muted"
    >
      <template v-if="showAuthor">
        <AuthorHandle :username="clip.author.username" class="min-w-0 shrink truncate text-ink" />
        <span class="shrink-0">·</span>
      </template>
      <span class="shrink-0">{{ formatRelativeTime(clip.createdAt) }}</span>
      <span class="ml-auto flex shrink-0 items-center gap-2.5">
        <span class="inline-flex items-center gap-1">
          <IconEye :size="11" />
          {{ formatNum(clip.viewCount) }}
        </span>
        <span class="inline-flex items-center gap-1">
          <IconHeart :size="11" />
          {{ formatNum(clip.likeCount) }}
        </span>
      </span>
    </div>
  </article>
</template>
