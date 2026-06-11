<script setup lang="ts">
import { computed } from 'vue'
import type { ClipFeedItem } from '@/api/clips'
import { formatNum, formatRelativeTime } from '@/lib/format'
import UserAvatar from './UserAvatar.vue'
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
        :src="props.clip.thumbnailUrl"
        alt=""
        class="h-full w-full object-cover transition-transform duration-400 group-hover:scale-104"
      />
      <!-- Game tag — top-left. Links to /game/:slug. The handlers stop both
           pointer and keyboard activation from bubbling to the parent article,
           whose @click + @keydown otherwise route to the clip detail instead. -->
      <RouterLink
        v-if="props.clip.game"
        :to="{ name: 'game-detail', params: { slug: props.clip.game.slug } }"
        :aria-label="`Browse ${props.clip.game.name} clips`"
        class="absolute left-2 top-2 rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-brand"
        @click.stop
        @keydown="onLinkKeydown"
      >
        <GameTag :tag="props.clip.game.tag" />
      </RouterLink>
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

      <!-- Tag row: up to 3 chips + "+N" overflow indicator. Hidden when the
           clip has no tags so cards without tags don't grow a blank row. The
           overflow chip is a plain span — it isn't a link to any tag in
           particular, just a visual cue that more exist on the detail page.
           Both elements .stop the click so the chip area doesn't double as a
           card-click target (TagChip's internal RouterLink also .stops, but
           the redundancy makes the contract obvious from this template). -->
      <div v-if="clip.tags.length" class="flex flex-wrap gap-1.5">
        <TagChip v-for="t in visibleTags" :key="t.id" :slug="t.slug" :name="t.name" @click.stop />
        <span
          v-if="overflowCount > 0"
          class="rounded-[3px] border border-border-strong bg-surface-base px-1.5 py-0.5 font-mono text-[10px] font-medium uppercase tracking-[0.06em] text-text-muted"
          @click.stop
        >
          +{{ overflowCount }}
        </span>
      </div>

      <div
        v-if="showAuthor"
        class="flex items-center gap-2 overflow-hidden font-mono text-[11px] text-text-secondary"
      >
        <UserAvatar :user="clip.author" :size="20" />
        <AuthorHandle :username="clip.author.username" class="min-w-0 shrink truncate text-neon" />
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
        <span v-if="!showAuthor" class="ml-auto">{{ formatRelativeTime(clip.createdAt) }} ago</span>
      </div>
    </div>
  </article>
</template>
