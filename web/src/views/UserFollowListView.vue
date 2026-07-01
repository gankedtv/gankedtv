<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { follows, type UserSummary } from '@/api/follows'
import UserAvatar from '@/components/UserAvatar.vue'
import StatusPanel from '@/components/StatusPanel.vue'
import PageHeader from '@/components/PageHeader.vue'
import UnderlineTabs from '@/components/UnderlineTabs.vue'
import LoadMoreButton from '@/components/LoadMoreButton.vue'

const route = useRoute()
const router = useRouter()

type Kind = 'followers' | 'following'
// Route meta carries the static "kind" of the list so a single view component
// services both /user/:username/followers and /user/:username/following without
// the URL-path sniffing being duplicated here.
const kind = computed<Kind>(() => route.meta.kind ?? 'followers')

const username = computed(() => {
  const u = route.params.username
  return Array.isArray(u) ? u[0] : u
})

const items = ref<UserSummary[]>([])
const cursor = ref<string | null>(null)
const loading = ref(false)
const errored = ref(false)
const paginationErrored = ref(false)
// Tracks "is this the first page for the current (username, kind) combo" so
// route switches reset state cleanly without confusing initial-vs-pagination
// error branches.
let latestLoadKey = ''

const heading = computed(() => (kind.value === 'followers' ? 'Followers' : 'Following'))

async function loadMore() {
  if (!username.value) return
  const name = username.value
  const k = kind.value
  const key = `${name}|${k}`
  // Allow re-entry when the route key changes (tab toggle or username change)
  // even if a previous fetch is still in flight. The stale fetch's drift check
  // will discard its response. Without this carve-out, the watcher's loadMore()
  // would be blocked here and the UI would stay on the previous list.
  if (loading.value && key === latestLoadKey) return
  const isFirstPage = key !== latestLoadKey || items.value.length === 0
  if (isFirstPage) {
    latestLoadKey = key
    items.value = []
    cursor.value = null
    errored.value = false
  }
  paginationErrored.value = false
  loading.value = true
  try {
    const fetcher = k === 'followers' ? follows.listFollowers : follows.listFollowing
    const page = await fetcher(name, { cursor: cursor.value, limit: 20 })
    // Discard if the route changed mid-flight.
    if (latestLoadKey !== key) return
    items.value.push(...page.items)
    cursor.value = page.nextCursor
  } catch (err) {
    if (latestLoadKey !== key) return
    if (err instanceof ApiError && err.status === 404) {
      router.replace({ name: 'not-found' })
      return
    }
    if (isFirstPage) errored.value = true
    else paginationErrored.value = true
  } finally {
    if (latestLoadKey === key) loading.value = false
  }
}

// Re-fetch when either the username changes (route nav) or the kind changes
// (user switches between /followers and /following on the same profile).
watch([username, kind], () => loadMore(), { immediate: false })

onMounted(loadMore)
</script>

<template>
  <main class="mx-auto w-full max-w-3xl px-7 pt-7 pb-16 max-tablet:px-4">
    <RouterLink
      :to="{ name: 'user', params: { username } }"
      class="mb-4 inline-block text-[11px] font-semibold text-accent no-underline hover:underline"
    >
      ← Back to @{{ username }}
    </RouterLink>

    <PageHeader :title="heading">
      <template #caption>
        <span class="text-accent">@{{ username }}</span>
      </template>
    </PageHeader>

    <UnderlineTabs
      class="mt-6"
      :tabs="[
        {
          key: 'followers',
          label: 'Followers',
          to: { name: 'user-followers', params: { username } },
        },
        {
          key: 'following',
          label: 'Following',
          to: { name: 'user-following', params: { username } },
        },
      ]"
      :active="kind"
    />

    <StatusPanel
      v-if="loading && items.length === 0 && !errored"
      kind="loading"
      message="Loading"
    />

    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load this list.">
      <button
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="loadMore"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel
      v-else-if="!loading && items.length === 0"
      kind="empty"
      :message="
        kind === 'followers' ? 'No followers yet.' : `@${username} isn't following anyone yet.`
      "
    />

    <template v-else>
      <!-- Border-separated user rows, no card chrome. -->
      <ul class="m-0 mt-4 grid grid-cols-1 p-0">
        <li v-for="u in items" :key="u.id" class="list-none border-b border-border">
          <RouterLink
            :to="{ name: 'user', params: { username: u.username } }"
            class="group flex items-center gap-3 px-1 py-3 no-underline transition-colors duration-150 hover:bg-surface-raised"
          >
            <UserAvatar :user="u" :size="36" />
            <div class="flex min-w-0 flex-col">
              <span
                class="truncate text-sm font-semibold text-text-primary transition-colors duration-150 group-hover:text-accent"
                >{{ u.username }}</span
              >
              <span class="truncate text-[11px] text-accent">@{{ u.username }}</span>
            </div>
          </RouterLink>
        </li>
      </ul>

      <LoadMoreButton
        v-if="cursor || paginationErrored"
        class="mt-6"
        :loading="loading"
        :errored="paginationErrored"
        @load="loadMore"
      />
    </template>
  </main>
</template>
