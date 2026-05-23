<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useNotificationsStore } from '@/stores/notifications'
import type { NotificationItem } from '@/api/notifications'
import PageHeader from '@/components/PageHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import LoadMoreButton from '@/components/LoadMoreButton.vue'
import NotificationRow from '@/components/notifications/NotificationRow.vue'

const store = useNotificationsStore()
const router = useRouter()
const paginationErrored = ref(false)

onMounted(() => {
  // Refresh from the server when the page mounts — the dropdown's cached items may be stale,
  // and the user came here specifically to see the latest.
  void store.loadFirstPage(20)
})

function destinationFor(n: NotificationItem) {
  if (n.type === 'follow') {
    return { name: 'user', params: { username: n.actor.username } }
  }
  if (n.clip) {
    return { name: 'clip', params: { id: n.clip.id } }
  }
  return { name: 'user', params: { username: n.actor.username } }
}

function onRowClick(n: NotificationItem) {
  // Fire-and-forget — see NotificationsDropdown.onRowClick for the rationale.
  void store.markOneRead(n.id)
  void router.push(destinationFor(n))
}

async function onLoadMore() {
  paginationErrored.value = false
  const before = store.errored
  await store.loadMore(20)
  if (!before && store.errored) {
    paginationErrored.value = true
    store.errored = false
  }
}
</script>

<template>
  <main class="mx-auto max-w-3xl px-6 pt-8 pb-30 max-[899px]:px-3.5 max-[899px]:pt-4">
    <PageHeader title="Notifications">
      <template #caption>
        <span>Recent activity</span>
      </template>
      <div class="mt-3 flex">
        <button
          type="button"
          class="cursor-pointer rounded-sm border border-border bg-surface-raised px-3 py-1.5 font-mono text-[11px] uppercase tracking-[0.04em] text-text-secondary transition-colors duration-150 hover:border-border-hover hover:text-text-primary disabled:cursor-not-allowed disabled:opacity-40"
          :disabled="store.unreadCount === 0"
          @click="store.markAllRead()"
        >
          Mark all read
        </button>
      </div>
    </PageHeader>

    <StatusPanel
      v-if="store.loading && store.items.length === 0 && !store.errored"
      kind="loading"
      message="Loading…"
    />

    <StatusPanel v-else-if="store.errored" kind="error" message="Couldn't load notifications.">
      <button
        class="cursor-pointer rounded-sm border border-border bg-surface-raised px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary"
        @click="store.loadFirstPage(20)"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel
      v-else-if="!store.loading && store.items.length === 0"
      kind="empty"
      message="No notifications yet."
    />

    <template v-else>
      <ul class="mt-6 grid grid-cols-1 gap-2 p-0">
        <li
          v-for="n in store.items"
          :key="n.id"
          class="list-none cursor-pointer rounded-sm border border-border bg-surface-raised transition-colors duration-150 hover:border-border-hover"
          :class="n.readAt === null ? 'border-l-2 border-l-brand' : ''"
          @click="onRowClick(n)"
        >
          <NotificationRow :notification="n" />
        </li>
      </ul>

      <LoadMoreButton
        v-if="store.cursor || paginationErrored"
        class="mt-6"
        :loading="store.loadingMore"
        :errored="paginationErrored"
        @load="onLoadMore"
      />
    </template>
  </main>
</template>
