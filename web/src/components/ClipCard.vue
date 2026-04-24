<script setup lang="ts">
import { computed } from 'vue'
import { USERS, GAMES, formatNum, formatDuration, type Clip } from '@/lib/mock-data'
import UserAvatar from './UserAvatar.vue'

const props = defineProps<{ clip: Clip }>()
const emit = defineEmits<{ click: [] }>()

const user = computed(() => USERS[props.clip.user])
const game = computed(() => GAMES[props.clip.game])
</script>

<template>
  <article
    class="relative flex flex-col overflow-hidden cursor-pointer rounded-[var(--radius-md)] border border-border bg-surface-raised transition-all duration-200 hover:-translate-y-0.5 hover:border-brand hover:shadow-[0_14px_40px_-14px_var(--color-brand-glow)]"
    @click="emit('click')"
  >
    <!-- Thumbnail -->
    <div class="relative aspect-video bg-surface-sunken overflow-hidden">
      <img
        :src="clip.art"
        alt=""
        class="w-full h-full object-cover transition-transform duration-[400ms] group-hover:scale-[1.04]"
      />
      <!-- Game tag — top-left -->
      <div class="absolute top-2 left-2">
        <span class="game-tag">{{ game.tag }}</span>
      </div>
      <!-- Duration — bottom-right -->
      <div class="absolute bottom-2 right-2 clip-corner">
        {{ formatDuration(clip.duration) }}
      </div>
    </div>

    <!-- Body -->
    <div class="flex flex-col gap-2 px-3.5 pt-3 pb-3.5">
      <h3
        class="m-0 font-body text-sm font-medium text-text-primary leading-[1.35] line-clamp-2 min-h-[2.7em]"
      >
        {{ clip.title }}
      </h3>

      <div
        class="flex items-center gap-2 overflow-hidden"
        style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-secondary)"
      >
        <UserAvatar :user="clip.user" :size="20" />
        <span class="truncate min-w-0 shrink text-neon">@{{ user.username }}</span>
        <span class="shrink-0 text-text-muted">·</span>
        <span class="shrink-0">{{ clip.createdAt }} ago</span>
      </div>

      <div
        class="flex gap-2.5 pt-1.5 border-t border-dashed border-border"
        style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-muted)"
      >
        <span class="inline-flex items-center gap-1">
          <svg width="11" height="11" viewBox="0 0 24 24" fill="currentColor">
            <path
              d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z"
            />
          </svg>
          {{ formatNum(clip.likes) }}
        </span>
        <span class="inline-flex items-center gap-1">
          <svg width="11" height="11" viewBox="0 0 24 24" fill="currentColor">
            <path
              d="M12 4.5C7 4.5 2.73 7.61 1 12c1.73 4.39 6 7.5 11 7.5s9.27-3.11 11-7.5c-1.73-4.39-6-7.5-11-7.5zM12 17a5 5 0 110-10 5 5 0 010 10zm0-8a3 3 0 100 6 3 3 0 000-6z"
            />
          </svg>
          {{ formatNum(clip.views) }}
        </span>
      </div>
    </div>
  </article>
</template>

<style scoped>
.game-tag {
  font-family: var(--font-mono);
  font-size: 10px;
  font-weight: 500;
  letter-spacing: 0.06em;
  padding: 3px 7px;
  background: var(--color-surface-base);
  color: var(--color-text-primary);
  border: 1px solid var(--color-border-strong);
  border-radius: 3px;
  text-transform: uppercase;
}

.clip-corner {
  font-family: var(--font-mono);
  font-size: 10px;
  letter-spacing: 0.05em;
  color: #fff;
  background: rgba(0, 0, 0, 0.75);
  backdrop-filter: blur(4px);
  padding: 4px 7px;
  border-radius: 3px;
  line-height: 1;
}
</style>
