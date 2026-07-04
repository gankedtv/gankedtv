<script setup lang="ts">
import { computed } from 'vue'
import type { CommentItem } from '@/api/comments'
import { formatRelativeTime } from '@/lib/format'
import UserAvatar from '@/components/UserAvatar.vue'

const props = defineProps<{
  comment: CommentItem
  currentUserId: string | null
  // Top-level rows expose a Reply affordance; replies (one level deep) do not.
  canReply: boolean
}>()

const emit = defineEmits<{
  delete: [id: string]
  reply: [id: string]
  report: [id: string]
}>()

const isOwn = computed(
  () => !props.comment.deleted && props.currentUserId === props.comment.author.id,
)
</script>

<template>
  <div class="flex gap-3">
    <UserAvatar :user="comment.author" :size="32" class="mt-0.5" />
    <div class="min-w-0 flex-1">
      <div class="flex items-baseline gap-2">
        <span class="text-xs font-semibold text-accent">@{{ comment.author.username }}</span>
        <span class="text-[10.5px] text-text-muted">{{
          formatRelativeTime(comment.createdAt)
        }}</span>
      </div>

      <p v-if="comment.deleted" class="mt-0.5 text-sm italic leading-relaxed text-text-muted">
        [deleted]
      </p>
      <p
        v-else
        class="mt-0.5 whitespace-pre-wrap break-words text-sm leading-relaxed text-text-secondary"
      >
        {{ comment.body }}
      </p>

      <div class="mt-1 flex items-center gap-3">
        <button
          v-if="canReply"
          type="button"
          class="cursor-pointer text-[11px] font-medium text-text-muted transition-colors duration-150 hover:text-accent"
          @click="emit('reply', comment.id)"
        >
          Reply
        </button>
        <button
          v-if="isOwn"
          type="button"
          class="cursor-pointer text-[11px] font-medium text-text-muted transition-colors duration-150 hover:text-accent"
          @click="emit('delete', comment.id)"
        >
          Delete
        </button>
        <button
          v-if="!isOwn && currentUserId && !comment.deleted"
          type="button"
          class="cursor-pointer text-[11px] font-medium text-text-muted transition-colors duration-150 hover:text-accent"
          @click="emit('report', comment.id)"
        >
          Report
        </button>
      </div>
    </div>
  </div>
</template>
