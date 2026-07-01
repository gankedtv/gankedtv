<script setup lang="ts">
import { onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useNotificationsStore } from '@/stores/notifications'
import type { NotificationItem } from '@/api/notifications'
import NotificationRow from './NotificationRow.vue'

const store = useNotificationsStore()
const router = useRouter()
const emit = defineEmits<{ close: [] }>()

// Lazily load the first page when the dropdown opens — the polled unread-count is enough to
// drive the badge; the full list only matters when the user actually looks at it.
onMounted(() => {
  void store.loadFirstPage(10)
})

function destinationFor(n: NotificationItem) {
  // Like/comment → the clip detail; follow → the actor's profile.
  if (n.type === 'follow') {
    return { name: 'user', params: { username: n.actor.username } }
  }
  if (n.clip) {
    return { name: 'clip', params: { id: n.clip.id } }
  }
  // Defensive: a comment/like notification without a clip means the clip was deleted. Send
  // the user to the actor's profile rather than a broken clip route.
  return { name: 'user', params: { username: n.actor.username } }
}

function onRowClick(n: NotificationItem) {
  // Fire-and-forget: the store paints the row read optimistically, so awaiting the network
  // round-trip would only delay navigation. A failed mark-read reconciles on the next poll.
  void store.markOneRead(n.id)
  emit('close')
  void router.push(destinationFor(n))
}

function onMarkAll() {
  void store.markAllRead()
}

function onSeeAll() {
  emit('close')
  void router.push({ name: 'notifications' })
}
</script>

<template>
  <div class="overflow-hidden rounded-lg border border-border-strong bg-surface-base">
    <div class="flex items-center gap-2 border-b border-border px-4 py-2.5">
      <span
        class="font-condensed text-[13px] font-extrabold uppercase tracking-wide text-text-primary"
      >
        Notifications
      </span>
      <span
        v-if="store.unreadCount > 0"
        class="rounded-full border border-accent-border bg-accent-bg px-1.5 py-0.5 text-[9px] font-bold text-accent"
      >
        {{ store.unreadCount }} new
      </span>
      <button
        type="button"
        class="ml-auto cursor-pointer border-0 bg-transparent text-[11px] font-medium text-text-muted transition-colors duration-150 hover:text-accent disabled:cursor-not-allowed disabled:opacity-40"
        :disabled="store.unreadCount === 0"
        @click="onMarkAll"
      >
        Mark all read
      </button>
    </div>

    <div
      v-if="store.loading && store.items.length === 0"
      class="px-4 py-6 text-center text-[11px] text-text-muted"
    >
      Loading…
    </div>
    <div
      v-else-if="store.items.length === 0"
      class="px-4 py-6 text-center text-[11px] text-text-muted"
    >
      No notifications yet
    </div>
    <ul v-else class="m-0 max-h-96 list-none overflow-y-auto p-0">
      <li
        v-for="n in store.items"
        :key="n.id"
        class="cursor-pointer border-b border-border px-3 transition-colors duration-150 last:border-b-0"
        :class="n.readAt === null ? 'bg-surface-high hover:bg-surface-high/70' : 'hover:bg-surface-high'"
        @click="onRowClick(n)"
      >
        <NotificationRow :notification="n" />
      </li>
    </ul>

    <button
      type="button"
      class="block w-full cursor-pointer border-0 border-t border-border bg-transparent px-4 py-2 text-center text-[11px] font-semibold text-accent transition-colors duration-150 hover:bg-accent-bg"
      @click="onSeeAll"
    >
      See all notifications →
    </button>
  </div>
</template>
