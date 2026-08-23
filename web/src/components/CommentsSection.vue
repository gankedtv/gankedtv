<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { comments, type CommentItem } from '@/api/comments'
import { useAuthStore } from '@/stores/auth'
import CommentItemRow from '@/components/CommentItem.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ReportDialog from '@/components/ReportDialog.vue'
import SectionHeader from '@/components/SectionHeader.vue'

const props = defineProps<{ clipId: string }>()

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const currentUserId = computed(() => auth.user?.id ?? null)

const threads = ref<CommentItem[]>([])
const nextCursor = ref<string | null>(null)
const loading = ref(false)
const loadingMore = ref(false)
const errored = ref(false)
const actionError = ref('')

// Top-level composer
const newBody = ref('')
const posting = ref(false)

// Per-thread reply state
const replyingTo = ref<string | null>(null)
const replyBody = ref('')
const replyPosting = ref(false)
// Cursor for paging additional replies on a thread ("Show more replies").
const replyCursors = ref<Record<string, string | null>>({})
// Per-thread in-flight guard so rapid "Show more" clicks can't fire overlapping requests.
const replyLoading = ref<Record<string, boolean>>({})
// Per-comment like guard, keyed the same way — a single boolean would block liking a second
// comment while the first is still in flight.
const likeBusy = ref<Record<string, boolean>>({})

// Delete confirmation
const pendingDelete = ref<string | null>(null)
const deleting = ref(false)

// Report dialog — same component used for clips/users elsewhere, just pointed at the
// comment via the targetType prop.
const reportingId = ref<string | null>(null)
function requestReport(id: string) {
  reportingId.value = id
}

const inputClass =
  'w-full rounded-md border border-border bg-surface-high px-3 py-2.5 text-sm text-text-primary outline-none placeholder:text-text-muted transition-colors duration-150 focus:border-accent'

// Monotonic token guarding against stale list/loadMore responses when props.clipId
// flips mid-request. Captured pre-await; any response whose token no longer matches
// the latest is silently dropped so it can't overwrite the new clip's state.
let latestRequestId = 0

watch(
  () => props.clipId,
  () => load(),
  { immediate: true },
)

async function load() {
  const requestId = ++latestRequestId
  loading.value = true
  errored.value = false
  threads.value = []
  nextCursor.value = null
  replyCursors.value = {}
  try {
    const page = await comments.list(props.clipId)
    if (requestId !== latestRequestId) return
    threads.value = page.items
    nextCursor.value = page.nextCursor
    seedReplyCursors(page.items)
  } catch {
    if (requestId !== latestRequestId) return
    errored.value = true
  } finally {
    if (requestId === latestRequestId) loading.value = false
  }
}

async function loadMore() {
  if (!nextCursor.value || loadingMore.value) return
  const requestId = ++latestRequestId
  loadingMore.value = true
  try {
    const page = await comments.list(props.clipId, { cursor: nextCursor.value })
    if (requestId !== latestRequestId) return
    threads.value.push(...page.items)
    nextCursor.value = page.nextCursor
    seedReplyCursors(page.items)
  } catch {
    if (requestId !== latestRequestId) return
    actionError.value = 'Could not load more comments — try again.'
  } finally {
    if (requestId === latestRequestId) loadingMore.value = false
  }
}

function goLogin() {
  router.push({ name: 'login', query: { redirect: route.fullPath } })
}

// `crypto.randomUUID` keeps temp ids unique even on rapid double-submits (the previous
// `temp-${Date.now()}` collides at millisecond granularity).
function buildOptimistic(body: string, parentId: string | null): CommentItem {
  // postComment / postReply both gate on `auth.user` before calling this, so `auth.user!`
  // is safe here.
  const user = auth.user!
  return {
    id: `temp-${crypto.randomUUID()}`,
    body,
    author: { id: user.id, username: user.username, avatarUrl: user.avatarUrl },
    parentId,
    createdAt: new Date().toISOString(),
    replyCount: 0,
    replies: [],
    repliesNextCursor: null,
    deleted: false,
    likeCount: 0,
    likedByMe: false,
  }
}

// Seed the per-thread "show more replies" cursor from each thread's inline preview so the
// first click pages forward instead of re-fetching the preview rows the server already sent.
function seedReplyCursors(threadsToSeed: CommentItem[]) {
  for (const thread of threadsToSeed) {
    if (thread.repliesNextCursor) {
      replyCursors.value[thread.id] = thread.repliesNextCursor
    }
  }
}

async function postComment() {
  const body = newBody.value.trim()
  if (!body || posting.value || !auth.user) return

  posting.value = true
  actionError.value = ''
  // Optimistic: show the comment immediately under a temp id, reconcile on response.
  const optimistic = buildOptimistic(body, null)
  threads.value.unshift(optimistic)
  newBody.value = ''

  try {
    const created = await comments.create(props.clipId, { body })
    const idx = threads.value.findIndex((c) => c.id === optimistic.id)
    if (idx !== -1) threads.value[idx] = created
  } catch {
    threads.value = threads.value.filter((c) => c.id !== optimistic.id)
    newBody.value = body
    actionError.value = 'Could not post your comment — try again.'
  } finally {
    posting.value = false
  }
}

function toggleReply(commentId: string) {
  if (!auth.user) return goLogin()
  replyingTo.value = replyingTo.value === commentId ? null : commentId
  replyBody.value = ''
}

async function postReply(parentId: string) {
  const body = replyBody.value.trim()
  if (!body || replyPosting.value || !auth.user) return

  const thread = threads.value.find((c) => c.id === parentId)
  if (!thread) return

  replyPosting.value = true
  actionError.value = ''
  // Optimistic: render the reply immediately and reconcile on response, matching postComment.
  const optimistic = buildOptimistic(body, parentId)
  thread.replies.push(optimistic)
  thread.replyCount += 1
  replyingTo.value = null
  replyBody.value = ''

  try {
    const created = await comments.create(props.clipId, { body, parentId })
    const idx = thread.replies.findIndex((r) => r.id === optimistic.id)
    if (idx !== -1) thread.replies[idx] = created
  } catch {
    const idx = thread.replies.findIndex((r) => r.id === optimistic.id)
    if (idx !== -1) thread.replies.splice(idx, 1)
    thread.replyCount = Math.max(0, thread.replyCount - 1)
    // Reopen the composer with the failed body so the user can retry without retyping.
    replyingTo.value = parentId
    replyBody.value = body
    actionError.value = 'Could not post your reply — try again.'
  } finally {
    replyPosting.value = false
  }
}

async function showMoreReplies(thread: CommentItem) {
  if (replyLoading.value[thread.id]) return
  replyLoading.value[thread.id] = true
  try {
    const page = await comments.listReplies(thread.id, {
      cursor: replyCursors.value[thread.id] ?? undefined,
    })
    const known = new Set(thread.replies.map((r) => r.id))
    for (const reply of page.items) {
      if (!known.has(reply.id)) thread.replies.push(reply)
    }
    replyCursors.value[thread.id] = page.nextCursor
  } catch {
    actionError.value = 'Could not load replies — try again.'
  } finally {
    replyLoading.value[thread.id] = false
  }
}

function requestDelete(commentId: string) {
  pendingDelete.value = commentId
}

async function confirmDelete() {
  const id = pendingDelete.value
  if (!id) return
  deleting.value = true
  try {
    await comments.delete(id)
    markDeleted(id)
    pendingDelete.value = null
  } catch (err) {
    // A 404 means it's already gone — treat as success so the UI converges.
    if (err instanceof ApiError && err.status === 404) {
      markDeleted(id)
      pendingDelete.value = null
    } else {
      actionError.value = 'Could not delete the comment — try again.'
    }
  } finally {
    deleting.value = false
  }
}

async function toggleLike(commentId: string) {
  if (!auth.user) return goLogin()
  if (likeBusy.value[commentId]) return

  const target = findComment(commentId)
  if (!target || target.deleted) return

  const wasLiked = target.likedByMe
  likeBusy.value[commentId] = true
  target.likedByMe = !wasLiked
  target.likeCount = Math.max(0, target.likeCount + (wasLiked ? -1 : 1))

  try {
    const result = wasLiked ? await comments.unlike(commentId) : await comments.like(commentId)
    // Re-locate rather than reusing `target`: the clip can change mid-request, in which case
    // the row we optimistically edited belongs to a list that is no longer rendered.
    const current = findComment(commentId)
    if (current) {
      current.likeCount = result.likeCount
      current.likedByMe = result.liked
    }
  } catch {
    const current = findComment(commentId)
    if (current) {
      current.likedByMe = wasLiked
      current.likeCount = Math.max(0, current.likeCount + (wasLiked ? 1 : -1))
    }
    actionError.value = 'Could not update your like — try again.'
  } finally {
    delete likeBusy.value[commentId]
  }
}

// Threads are two levels deep, so a linear walk over both is enough.
function findComment(id: string): CommentItem | null {
  for (const thread of threads.value) {
    if (thread.id === id) return thread
    const reply = thread.replies.find((r) => r.id === id)
    if (reply) return reply
  }
  return null
}

// Soft-delete in place: top-level rows stay visible as `[deleted]` so replies don't
// collapse; a deleted reply is dropped (the server excludes it on the next load).
function markDeleted(id: string) {
  for (const thread of threads.value) {
    if (thread.id === id) {
      thread.deleted = true
      thread.body = null
      return
    }
    const idx = thread.replies.findIndex((r) => r.id === id)
    if (idx !== -1) {
      thread.replies.splice(idx, 1)
      thread.replyCount = Math.max(0, thread.replyCount - 1)
      return
    }
  }
}
</script>

<template>
  <section class="mt-10 max-w-190">
    <div class="mb-5">
      <SectionHeader roman="III" kicker="The Record" title="Comments" />
    </div>

    <!-- Composer / login CTA -->
    <div class="mb-5">
      <form v-if="auth.isAuthenticated" @submit.prevent="postComment">
        <textarea
          v-model="newBody"
          rows="2"
          maxlength="2000"
          placeholder="Add a comment…"
          aria-label="Add a comment"
          :class="inputClass"
          class="resize-y"
        />
        <div class="mt-2 flex justify-end">
          <button
            type="submit"
            :disabled="posting || newBody.trim().length === 0"
            class="cursor-pointer rounded-md bg-accent px-3.5 py-2 text-[11px] font-bold text-[#080f0d] transition-[filter] duration-150 hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {{ posting ? 'Posting…' : 'Comment' }}
          </button>
        </div>
      </form>
      <button
        v-else
        type="button"
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2.5 text-xs font-medium text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="goLogin"
      >
        Log in to comment
      </button>
    </div>

    <p v-if="actionError" class="mb-3 text-xs font-semibold text-accent">
      {{ actionError }}
    </p>

    <!-- List -->
    <div v-if="loading" class="text-xs text-text-muted">Loading comments…</div>
    <div v-else-if="errored" class="text-xs font-semibold text-accent">
      Could not load comments.
    </div>
    <div v-else-if="threads.length === 0" class="text-xs text-text-muted">
      No comments yet. Be the first.
    </div>

    <ul v-else class="flex flex-col gap-5">
      <li v-for="thread in threads" :key="thread.id">
        <CommentItemRow
          :comment="thread"
          :current-user-id="currentUserId"
          :can-reply="auth.isAuthenticated"
          :like-busy="likeBusy[thread.id]"
          @reply="toggleReply"
          @delete="requestDelete"
          @report="requestReport"
          @like="toggleLike"
        />

        <!-- Reply composer -->
        <form
          v-if="replyingTo === thread.id"
          class="mt-2 pl-11"
          @submit.prevent="postReply(thread.id)"
        >
          <textarea
            v-model="replyBody"
            rows="2"
            maxlength="2000"
            placeholder="Write a reply…"
            aria-label="Write a reply"
            :class="inputClass"
            class="resize-y"
          />
          <div class="mt-2 flex justify-end gap-2">
            <button
              type="button"
              class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-3 py-1.5 text-xs font-medium text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
              @click="replyingTo = null"
            >
              Cancel
            </button>
            <button
              type="submit"
              :disabled="replyPosting || replyBody.trim().length === 0"
              class="cursor-pointer rounded-md bg-accent px-3 py-1.5 text-[11px] font-bold text-[#080f0d] transition-[filter] duration-150 hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {{ replyPosting ? 'Replying…' : 'Reply' }}
            </button>
          </div>
        </form>

        <!-- Replies -->
        <ul v-if="thread.replies.length > 0" class="mt-3 flex flex-col gap-4 pl-11">
          <li v-for="reply in thread.replies" :key="reply.id">
            <CommentItemRow
              :comment="reply"
              :current-user-id="currentUserId"
              :can-reply="false"
              :like-busy="likeBusy[reply.id]"
              @delete="requestDelete"
              @report="requestReport"
              @like="toggleLike"
            />
          </li>
        </ul>

        <button
          v-if="thread.replyCount > thread.replies.length"
          type="button"
          class="mt-2 ml-11 cursor-pointer text-[11px] font-semibold text-accent transition-colors duration-150 hover:underline"
          @click="showMoreReplies(thread)"
        >
          Show {{ thread.replyCount - thread.replies.length }} more
          {{ thread.replyCount - thread.replies.length === 1 ? 'reply' : 'replies' }}
        </button>
      </li>
    </ul>

    <button
      v-if="nextCursor"
      type="button"
      :disabled="loadingMore"
      class="mt-5 cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-medium text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:opacity-50"
      @click="loadMore"
    >
      {{ loadingMore ? 'Loading…' : 'Load more comments' }}
    </button>

    <ConfirmDialog
      :open="pendingDelete !== null"
      title="Delete comment?"
      body="This removes your comment. Replies stay visible as [deleted]."
      confirm-label="Delete"
      variant="danger"
      :busy="deleting"
      @cancel="pendingDelete = null"
      @confirm="confirmDelete"
    />

    <ReportDialog
      :open="reportingId !== null"
      target-type="comment"
      :target-id="reportingId ?? ''"
      @cancel="reportingId = null"
      @submitted="reportingId = null"
    />
  </section>
</template>
