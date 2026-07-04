<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  banUser,
  hideClip,
  listReports,
  removeComment,
  resolveReport,
  setClipGame,
  unbanUser,
  unhideClip,
  type ReportItem,
  type ReportStatus,
} from '@/api/admin'
import { ApiError } from '@/api/client'
import PageHeader from '@/components/PageHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'
import ReportRow from '@/components/admin/ReportRow.vue'
import FixGameDialog from '@/components/admin/FixGameDialog.vue'

const route = useRoute()
const router = useRouter()

const TABS: { key: ReportStatus; label: string }[] = [
  { key: 'open', label: 'Open' },
  { key: 'resolved', label: 'Resolved' },
  { key: 'dismissed', label: 'Dismissed' },
]

const status = ref<ReportStatus>('open')
const page = ref(1)
const items = ref<ReportItem[]>([])
const total = ref(0)
const loading = ref(false)
const error = ref<string | null>(null)
const actionError = ref<string | null>(null)

watch(
  () => route.query.status,
  (v) => {
    if (typeof v === 'string' && (v === 'open' || v === 'resolved' || v === 'dismissed')) {
      status.value = v
    }
  },
  { immediate: true },
)

onMounted(load)
watch([status, page], load)

// Monotonic request token. Switching tabs or paging triggers a new load() before the
// previous request resolves; without the fence, a slow earlier response can land after
// the fast later one and overwrite the freshly-painted page. Mirrors the same pattern
// in UserView (latestLoadId) and CommentsSection (latestRequestId).
let latestLoadId = 0

async function load() {
  const myLoadId = ++latestLoadId
  loading.value = true
  error.value = null
  try {
    const page$ = await listReports({ status: status.value, page: page.value })
    if (myLoadId !== latestLoadId) return
    items.value = page$.items
    total.value = page$.total
  } catch (err) {
    if (myLoadId !== latestLoadId) return
    if (err instanceof ApiError) {
      error.value = `Could not load reports (${err.status}).`
    } else {
      error.value = 'Could not load reports.'
    }
  } finally {
    if (myLoadId === latestLoadId) loading.value = false
  }
}

function setStatus(next: ReportStatus) {
  router.replace({ query: { ...route.query, status: next } })
  status.value = next
  page.value = 1
}

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / 20)))

async function withAction(fn: () => Promise<unknown>) {
  actionError.value = null
  try {
    await fn()
    await load()
  } catch (err) {
    actionError.value =
      err instanceof ApiError ? `Action failed (${err.status}).` : 'Action failed.'
  }
}

function onResolve(item: ReportItem, outcome: 'resolved' | 'dismissed') {
  return withAction(() => resolveReport(item.id, outcome))
}

function onHideClip(item: ReportItem) {
  return withAction(() => hideClip(item.targetId))
}

function onUnhideClip(item: ReportItem) {
  return withAction(() => unhideClip(item.targetId))
}

function onRemoveComment(item: ReportItem) {
  return withAction(() => removeComment(item.targetId))
}

function onBanUser(item: ReportItem) {
  return withAction(() => banUser(item.targetId))
}

function onUnbanUser(item: ReportItem) {
  return withAction(() => unbanUser(item.targetId))
}

// Fix-game flow: opens an empty picker (the existing wrong tag isn't a useful default —
// admin actively picks the correct one), then on submit posts to /admin/clips/{id}/game
// and the queue refreshes (the wrong_game report falls off the Open tab via reason-scoped
// auto-resolve on the server).
const fixGameFor = ref<ReportItem | null>(null)
function onFixGame(item: ReportItem) {
  fixGameFor.value = item
}
async function onFixGameSubmit(gameId: number | null) {
  const item = fixGameFor.value
  fixGameFor.value = null
  if (!item) return
  await withAction(() => setClipGame(item.targetId, gameId))
}
</script>

<template>
  <div class="mx-auto max-w-5xl px-7 pt-7 pb-16 max-tablet:px-4">
    <PageHeader title="Admin">
      <template #caption> <span class="text-accent">Moderation</span> · Report queue </template>
    </PageHeader>

    <!-- Tabs -->
    <UnderlineTabs class="mt-5 mb-6" :tabs="TABS" :active="status" @select="setStatus" />

    <p
      v-if="actionError"
      class="mb-4 rounded-lg border border-accent-border bg-accent-bg px-4 py-3 text-xs font-medium text-accent"
    >
      {{ actionError }}
    </p>
    <StatusPanel v-if="error" kind="error" :message="error" />
    <StatusPanel v-else-if="loading" kind="loading" message="Loading" />
    <StatusPanel v-else-if="items.length === 0" kind="empty" :message="`No ${status} reports.`" />
    <ul v-else class="m-0 flex list-none flex-col gap-3 p-0">
      <li v-for="item in items" :key="item.id">
        <ReportRow
          :item="item"
          @resolve="onResolve(item, 'resolved')"
          @dismiss="onResolve(item, 'dismissed')"
          @hide-clip="onHideClip(item)"
          @unhide-clip="onUnhideClip(item)"
          @fix-game="onFixGame(item)"
          @remove-comment="onRemoveComment(item)"
          @ban-user="onBanUser(item)"
          @unban-user="onUnbanUser(item)"
        />
      </li>
    </ul>

    <!-- Pagination -->
    <div v-if="pageCount > 1" class="mt-6 flex items-center justify-center gap-3">
      <button
        type="button"
        :disabled="page <= 1"
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:pointer-events-none disabled:opacity-40"
        @click="page--"
      >
        Prev
      </button>
      <span class="text-xs text-text-muted">{{ page }} / {{ pageCount }}</span>
      <button
        type="button"
        :disabled="page >= pageCount"
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:pointer-events-none disabled:opacity-40"
        @click="page++"
      >
        Next
      </button>
    </div>

    <FixGameDialog
      :open="fixGameFor !== null"
      @cancel="fixGameFor = null"
      @submit="onFixGameSubmit"
    />
  </div>
</template>
