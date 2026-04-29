<script setup lang="ts">
import type { ClipFeedItem } from '@/api/clips'
import { formatNum, formatRelativeTime } from '@/lib/format'
import UserAvatar from './UserAvatar.vue'
import GameTag from './GameTag.vue'
import DurationBadge from './DurationBadge.vue'
import AuthorHandle from './AuthorHandle.vue'
import IconHeart from './icons/IconHeart.vue'
import IconEye from './icons/IconEye.vue'

const props = defineProps<{ clip: ClipFeedItem }>()
const emit = defineEmits<{ click: [] }>()

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Enter' || e.key === ' ') {
    e.preventDefault()
    emit('click')
  }
}
</script>

<template>
  <article
    role="button"
    tabindex="0"
    :aria-label="clip.title"
    class="group relative flex cursor-pointer flex-col overflow-hidden rounded-md border border-border bg-surface-raised transition-all duration-200 outline-none hover:-translate-y-0.5 hover:border-brand hover:shadow-[0_14px_40px_-14px_var(--color-brand-glow)] focus-visible:border-brand focus-visible:shadow-[0_14px_40px_-14px_var(--color-brand-glow)]"
    @click="emit('click')"
    @keydown="onKeydown"
  >
    <!-- Thumbnail -->
    <div class="relative aspect-video overflow-hidden bg-surface-sunken">
      <img
        v-if="props.clip.thumbnailKey"
        :src="props.clip.thumbnailKey"
        alt=""
        class="h-full w-full object-cover transition-transform duration-400 group-hover:scale-104"
      />
      <div v-else class="h-full w-full bg-surface-sunken" />
      <!-- Game tag — top-left -->
      <div v-if="props.clip.game" class="absolute left-2 top-2">
        <GameTag :tag="props.clip.game.tag" />
      </div>
      <!-- Duration — bottom-right -->
      <DurationBadge :seconds="props.clip.durationSecs" class="absolute bottom-2 right-2" />
    </div>

    <!-- Body -->
    <div class="flex flex-col gap-2 px-3.5 pb-3.5 pt-3">
      <h3
        class="m-0 line-clamp-2 min-h-[2.7em] font-body text-sm font-medium leading-[1.35] text-text-primary"
      >
        {{ clip.title }}
      </h3>

      <div
        class="flex items-center gap-2 overflow-hidden font-mono text-[11px] text-text-secondary"
      >
        <UserAvatar :user="clip.author" :size="20" />
        <AuthorHandle
          :username="clip.author.username"
          class="min-w-0 shrink truncate text-neon"
        />
        <span class="shrink-0 text-text-muted">·</span>
        <span class="shrink-0">{{ formatRelativeTime(clip.createdAt) }} ago</span>
      </div>

      <div
        class="flex gap-2.5 border-t border-dashed border-border pt-1.5 font-mono text-[11px] text-text-muted"
      >
        <span class="inline-flex items-center gap-1">
          <IconHeart :size="11" />
          {{ formatNum(clip.likeCount) }}
        </span>
        <span class="inline-flex items-center gap-1">
          <IconEye :size="11" />
          {{ formatNum(clip.viewCount) }}
        </span>
      </div>
    </div>
  </article>
</template>
