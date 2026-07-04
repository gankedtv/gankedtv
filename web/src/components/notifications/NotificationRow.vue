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
  <div class="flex items-start gap-3 px-1 py-3.5">
    <UserAvatar :user="notification.actor" :size="30" />
    <div class="flex min-w-0 flex-1 flex-col">
      <p class="m-0 truncate text-sm text-text-primary">
        <span class="text-xs font-semibold text-accent">@{{ notification.actor.username }}</span>
        <span class="text-text-secondary"> {{ actionLabel }}</span>
        <span v-if="clipTitle" class="text-text-secondary"> &ldquo;{{ clipTitle }}&rdquo;</span>
      </p>
      <p v-if="commentSnippet" class="m-0 mt-0.5 truncate text-xs text-text-secondary italic">
        {{ commentSnippet }}
      </p>
    </div>
    <span class="mt-1 flex shrink-0 items-center gap-2">
      <span class="text-[10px] text-text-muted">
        {{ formatRelativeTime(notification.createdAt) }}
      </span>
      <span
        v-if="notification.readAt === null"
        class="size-1.75 rounded-full bg-accent"
        aria-label="Unread"
      ></span>
    </span>
  </div>
</template>
