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
    class="rounded-lg border border-border bg-surface-raised p-4 transition-colors duration-150 hover:border-border-strong"
  >
    <header class="mb-3 flex items-baseline justify-between gap-2">
      <div class="flex items-center gap-3">
        <span
          class="rounded-sm border border-border px-1.5 py-0.5 text-[10px] font-bold uppercase tracking-[0.07em] text-text-muted"
          >{{ item.targetType }}</span
        >
        <span
          class="rounded-sm border border-border bg-surface-high px-1.5 py-0.5 text-[10px] font-bold uppercase tracking-[0.07em] text-text-secondary"
          >{{ item.reason }}</span
        >
        <span class="text-[11px] text-text-muted">
          {{ new Date(item.createdAt).toLocaleString() }}
        </span>
      </div>
      <span
        v-if="!isOpen"
        class="text-[10px] font-bold uppercase tracking-[0.14em] text-text-muted"
        >{{ item.status }}</span
      >
    </header>

    <div class="mb-3 flex items-start gap-3">
      <component
        :is="targetLink ? 'a' : 'div'"
        :href="targetLink ?? undefined"
        target="_blank"
        rel="noopener"
        class="flex-1 text-sm text-text-primary"
      >
        <p class="line-clamp-2 break-words">{{ targetTitle }}</p>
        <p
          v-if="clipIsHidden"
          class="mt-1 w-fit rounded-sm border border-border-strong bg-surface-high px-1.5 py-0.5 text-[10px] font-bold text-text-primary"
        >
          Clip is hidden
        </p>
        <p
          v-if="userIsBanned"
          class="mt-1 w-fit rounded-sm border border-border-strong bg-surface-high px-1.5 py-0.5 text-[10px] font-bold text-text-primary"
        >
          User is banned
        </p>
      </component>
    </div>

    <p v-if="item.note" class="mb-3 border-l-2 border-border pl-3 text-xs text-text-secondary">
      {{ item.note }}
    </p>

    <footer class="flex flex-wrap items-center justify-between gap-2 border-t border-border pt-3">
      <p class="text-[11px] text-text-muted">
        by <span class="font-semibold text-accent">@{{ item.reporter.username }}</span>
      </p>
      <div v-if="isOpen" class="flex flex-wrap gap-2">
        <button
          v-if="item.targetType === 'clip' && item.reason === 'wrong_game'"
          type="button"
          @click="$emit('fixGame')"
          class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-3 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        >
          Fix game
        </button>
        <button
          v-if="item.targetType === 'clip'"
          type="button"
          @click="clipIsHidden ? $emit('unhideClip') : $emit('hideClip')"
          class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-3 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        >
          {{ clipIsHidden ? 'Unhide clip' : 'Hide clip' }}
        </button>
        <button
          v-if="item.targetType === 'comment'"
          type="button"
          @click="$emit('removeComment')"
          class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-3 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        >
          Remove comment
        </button>
        <button
          v-if="item.targetType === 'user' && auth.isAdmin"
          type="button"
          @click="userIsBanned ? $emit('unbanUser') : $emit('banUser')"
          class="cursor-pointer rounded-lg border border-accent-border bg-transparent px-3 py-1.5 text-xs font-semibold text-accent transition-colors duration-150 hover:border-accent hover:bg-accent-bg"
        >
          {{ userIsBanned ? 'Unban user' : 'Ban user' }}
        </button>
        <button
          type="button"
          @click="$emit('dismiss')"
          class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-3 py-1.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        >
          Dismiss
        </button>
        <button
          type="button"
          @click="$emit('resolve')"
          class="cursor-pointer rounded-lg bg-accent px-3 py-1.5 text-xs font-bold text-[#080f0d] transition-colors duration-150 hover:bg-accent/85"
        >
          Resolve
        </button>
      </div>
    </footer>
  </article>
</template>
