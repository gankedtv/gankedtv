<script setup lang="ts">
import { computed } from 'vue'
import type { ReportItem } from '@/api/admin'
import { useAuthStore } from '@/stores/auth'

const props = defineProps<{ item: ReportItem }>()
// Ban/unban is admin-only on the server (RolePolicies.Admin); mods clicking the button
// would just collect a 403 toast. Read the role here so the button never renders for
// moderators, matching how AppNav already gates the admin link.
const auth = useAuthStore()
defineEmits<{
  resolve: []
  dismiss: []
  hideClip: []
  unhideClip: []
  fixGame: []
  removeComment: []
  banUser: []
  unbanUser: []
}>()

const isOpen = computed(() => props.item.status === 'open')

const targetLink = computed(() => {
  const i = props.item
  if (i.targetType === 'clip' && i.target.clip) return `/clip/${i.target.clip.id}`
  if (i.targetType === 'comment' && i.target.comment) return `/clip/${i.target.comment.clipId}`
  if (i.targetType === 'user' && i.target.user) return `/user/${i.target.user.username}`
  return null
})

const targetTitle = computed(() => {
  const i = props.item
  if (i.targetType === 'clip') return i.target.clip?.title ?? '(deleted clip)'
  if (i.targetType === 'comment') return i.target.comment?.body ?? '(deleted comment)'
  if (i.targetType === 'user') return i.target.user?.username ?? '(deleted user)'
  return ''
})

const clipIsHidden = computed(() => props.item.target.clip?.visibility === 'hidden')
const userIsBanned = computed(
  () => props.item.target.user?.bannedAt !== null && props.item.target.user?.bannedAt !== undefined,
)
</script>

<template>
  <article
    class="border-b border-border px-1 py-4 transition-colors duration-150 hover:bg-surface-raised"
  >
    <header class="mb-3 flex items-baseline justify-between gap-2">
      <div class="flex items-center gap-3">
        <span
          class="border border-border px-2 py-0.5 font-mono text-[10px] uppercase tracking-wider text-text-secondary"
          >{{ item.targetType }}</span
        >
        <span
          class="border border-signal px-2 py-0.5 font-mono text-[10px] font-bold uppercase tracking-wider text-signal"
          >{{ item.reason }}</span
        >
        <span class="font-mono text-[10px] text-text-muted">
          {{ new Date(item.createdAt).toLocaleString() }}
        </span>
      </div>
      <span v-if="!isOpen" class="font-mono text-[10px] uppercase tracking-wider text-text-muted">{{
        item.status
      }}</span>
    </header>

    <div class="mb-3 flex items-start gap-3">
      <component
        :is="targetLink ? 'a' : 'div'"
        :href="targetLink ?? undefined"
        target="_blank"
        rel="noopener"
        class="flex-1 font-body text-sm text-text-primary"
      >
        <p class="line-clamp-2 break-words">{{ targetTitle }}</p>
        <p v-if="clipIsHidden" class="mt-1 font-mono text-[10px] text-signal">
          Clip is hidden
        </p>
        <p v-if="userIsBanned" class="mt-1 font-mono text-[10px] text-signal">
          User is banned
        </p>
      </component>
    </div>

    <p
      v-if="item.note"
      class="mb-3 border-l-2 border-border pl-3 font-mono text-xs text-text-secondary"
    >
      {{ item.note }}
    </p>

    <footer class="flex flex-wrap items-center justify-between gap-2 border-t border-border pt-3">
      <p class="font-mono text-[11px] text-text-muted">
        by <span class="text-ink">@{{ item.reporter.username }}</span>
      </p>
      <div v-if="isOpen" class="flex flex-wrap gap-2">
        <button
          v-if="item.targetType === 'clip' && item.reason === 'wrong_game'"
          type="button"
          @click="$emit('fixGame')"
          class="cursor-pointer border border-border bg-transparent px-3 py-1.5 font-mono text-[10px] uppercase tracking-[0.12em] text-text-secondary transition-colors duration-150 hover:border-ink hover:text-ink"
        >
          Fix game
        </button>
        <button
          v-if="item.targetType === 'clip'"
          type="button"
          @click="clipIsHidden ? $emit('unhideClip') : $emit('hideClip')"
          class="cursor-pointer border border-border bg-transparent px-3 py-1.5 font-mono text-[10px] uppercase tracking-[0.12em] text-text-secondary transition-colors duration-150 hover:border-ink hover:text-ink"
        >
          {{ clipIsHidden ? 'Unhide clip' : 'Hide clip' }}
        </button>
        <button
          v-if="item.targetType === 'comment'"
          type="button"
          @click="$emit('removeComment')"
          class="cursor-pointer border border-border bg-transparent px-3 py-1.5 font-mono text-[10px] uppercase tracking-[0.12em] text-text-secondary transition-colors duration-150 hover:border-ink hover:text-ink"
        >
          Remove comment
        </button>
        <button
          v-if="item.targetType === 'user' && auth.isAdmin"
          type="button"
          @click="userIsBanned ? $emit('unbanUser') : $emit('banUser')"
          class="cursor-pointer border border-ink bg-transparent px-3 py-1.5 font-mono text-[10px] font-bold uppercase tracking-[0.12em] text-ink transition-[filter] duration-150 hover:brightness-110"
        >
          {{ userIsBanned ? 'Unban user' : 'Ban user' }}
        </button>
        <button
          type="button"
          @click="$emit('dismiss')"
          class="cursor-pointer border border-border bg-transparent px-3 py-1.5 font-mono text-[10px] uppercase tracking-[0.12em] text-text-secondary transition-colors duration-150 hover:border-ink hover:text-ink"
        >
          Dismiss
        </button>
        <button
          type="button"
          @click="$emit('resolve')"
          class="cursor-pointer bg-ink px-3 py-1.5 font-mono text-[10px] font-bold uppercase tracking-[0.12em] text-signal-text transition-[filter] duration-150 hover:brightness-108"
        >
          Resolve
        </button>
      </div>
    </footer>
  </article>
</template>
