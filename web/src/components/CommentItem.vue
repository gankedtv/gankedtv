<script setup lang="ts">
import { computed } from 'vue'
import type { CommentItem } from '@/api/comments'
import { formatRelativeTime } from '@/lib/format'
import UserAvatar from '@/components/UserAvatar.vue'
import AuthorHandle from '@/components/AuthorHandle.vue'
import IconHeart from '@/components/icons/IconHeart.vue'

const props = defineProps<{
  comment: CommentItem
  currentUserId: string | null
  // Top-level rows expose a Reply affordance; replies (one level deep) do not.
  canReply: boolean
  likeBusy?: boolean
}>()

const emit = defineEmits<{
  delete: [id: string]
  reply: [id: string]
  report: [id: string]
  like: [id: string]
}>()

const isOwn = computed(
  () => !props.comment.deleted && props.currentUserId === props.comment.author.id,
)
</script>

<template>
  <div class="flex gap-3">
    <RouterLink
      :to="{ name: 'user', params: { username: comment.author.username } }"
      class="mt-0.5 shrink-0 transition-opacity hover:opacity-80"
    >
      <UserAvatar :user="comment.author" :size="32" />
    </RouterLink>
    <div class="min-w-0 flex-1">
      <div class="flex items-baseline gap-2">
        <AuthorHandle
          :username="comment.author.username"
          as="link"
          class="text-xs font-semibold text-accent"
        />
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
        <!-- Hidden on `[deleted]` rows: there is no body left to endorse, and the endpoint
             404s them anyway. The count only appears once someone has liked. -->
        <button
          v-if="!comment.deleted"
          type="button"
          class="flex cursor-pointer items-center gap-1 text-[11px] font-medium transition-colors duration-150"
          :class="comment.likedByMe ? 'text-accent' : 'text-text-muted hover:text-accent'"
          :aria-pressed="comment.likedByMe"
          :aria-label="comment.likedByMe ? 'Unlike comment' : 'Like comment'"
          :disabled="likeBusy"
          @click="emit('like', comment.id)"
        >
          <IconHeart :size="12" />
          <span v-if="comment.likeCount > 0">{{ comment.likeCount }}</span>
        </button>
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
