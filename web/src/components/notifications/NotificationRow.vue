<script setup lang="ts">
import { computed } from 'vue'
import type { NotificationItem } from '@/api/notifications'
import { formatRelativeTime } from '@/lib/format'
import UserAvatar from '@/components/UserAvatar.vue'

const props = defineProps<{ notification: NotificationItem }>()

const actionLabel = computed(() => {
  switch (props.notification.type) {
    case 'like':
      return 'liked your clip'
    case 'comment':
      return 'commented on your clip'
    case 'follow':
      return 'started following you'
    default:
      return ''
  }
})

const clipTitle = computed(() => props.notification.clip?.title ?? null)
const commentSnippet = computed(() => {
  const body = props.notification.comment?.body
  if (!body) return null
  // Trim a long comment so the dropdown row stays single-line.
  return body.length > 80 ? `${body.slice(0, 80)}…` : body
})
</script>

<template>
  <div class="flex items-start gap-3 px-4 py-3">
    <UserAvatar :user="notification.actor" :size="32" />
    <div class="flex min-w-0 flex-1 flex-col">
      <p class="m-0 truncate font-body text-sm text-text-primary">
        <span class="font-semibold">{{ notification.actor.username }}</span>
        <span class="text-text-secondary"> {{ actionLabel }}</span>
        <span v-if="clipTitle" class="text-text-secondary"> &ldquo;{{ clipTitle }}&rdquo;</span>
      </p>
      <p
        v-if="commentSnippet"
        class="m-0 mt-0.5 truncate font-body text-xs text-text-secondary italic"
      >
        {{ commentSnippet }}
      </p>
      <span class="mt-0.5 font-mono text-[11px] tracking-[0.04em] text-text-muted uppercase">
        {{ formatRelativeTime(notification.createdAt) }}
      </span>
    </div>
    <span
      v-if="notification.readAt === null"
      class="mt-1.5 inline-block h-2 w-2 shrink-0 rounded-full bg-brand"
      aria-label="Unread"
    ></span>
  </div>
</template>
