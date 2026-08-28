<script setup lang="ts">
import type { ClipFeedItem } from '@/api/clips'
import { formatNum, formatRelativeTime } from '@/lib/format'
import GameTag from './GameTag.vue'
import DurationBadge from './DurationBadge.vue'
import AuthorHandle from './AuthorHandle.vue'
import IconHeart from './icons/IconHeart.vue'
import IconEye from './icons/IconEye.vue'
import ThumbImage from './ThumbImage.vue'

const props = withDefaults(defineProps<{ clip: ClipFeedItem; showAuthor?: boolean }>(), {
  showAuthor: true,
})
const emit = defineEmits<{ click: [] }>()

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
  <!-- Chrome-less Arena card: raised surface + hairline border. Hover is a
       border shift — never a transform, shadow, or glow. -->
  <article
    role="button"
    tabindex="0"
    :aria-label="clip.title"
    class="group flex cursor-pointer flex-col overflow-hidden rounded-lg border border-border bg-surface-raised outline-none transition-colors duration-150 hover:border-border-strong focus-visible:border-accent"
    @click="emit('click')"
    @keydown="onKeydown"
  >
    <!-- Thumbnail -->
    <div class="relative aspect-video overflow-hidden bg-surface-high">
      <ThumbImage :src="props.clip.thumbnailUrl" class="h-full w-full object-cover" />
      <!-- Game tag — top-left. Links to /game/:slug. The handlers stop both
           pointer and keyboard activation from bubbling to the parent article,
           whose @click + @keydown otherwise route to the clip detail instead. -->
      <RouterLink
        v-if="props.clip.game"
        :to="{ name: 'game-detail', params: { slug: props.clip.game.slug } }"
        :aria-label="`Browse ${props.clip.game.name} clips`"
        class="absolute left-2 top-2 outline-none focus-visible:ring-2 focus-visible:ring-accent"
        @click.stop
        @keydown="onLinkKeydown"
      >
        <GameTag :tag="props.clip.game.tag" />
      </RouterLink>
      <!-- Duration — bottom-right -->
      <DurationBadge :seconds="props.clip.durationSecs" class="absolute bottom-2 right-2" />
    </div>

    <div class="flex flex-col gap-2 px-3 pb-3 pt-2.5">
      <!-- Title -->
      <h3 class="m-0 line-clamp-2 min-h-8 text-xs font-semibold leading-[1.3] text-text-primary">
        {{ clip.title }}
      </h3>

      <!-- Meta — @author in mint, stats right-aligned -->
      <div class="flex items-center gap-1.5 overflow-hidden text-[10px] text-text-muted">
        <template v-if="showAuthor">
          <AuthorHandle
            :username="clip.author.username"
            class="min-w-0 shrink truncate font-medium text-accent"
          />
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
    </div>
  </article>
</template>
