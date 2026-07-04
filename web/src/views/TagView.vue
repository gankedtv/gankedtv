<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { tags, type TagDetail } from '@/api/tags'
import type { ClipFeedItem } from '@/api/clips'
import { ApiError } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import ClipCard from '@/components/ClipCard.vue'
import PageHeader from '@/components/PageHeader.vue'
import StatusPanel from '@/components/StatusPanel.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const slug = computed(() => {
  const raw = route.params.slug
  return Array.isArray(raw) ? raw[0] : raw
})

const tag = ref<TagDetail | null>(null)
const items = ref<ClipFeedItem[]>([])
const cursor = ref<string | null>(null)
const reachedEnd = ref(false)
const loading = ref(false)
const initialLoading = ref(true)
const errored = ref(false)
const notFound = ref(false)
const paginationErrored = ref(false)

const sentinel = ref<HTMLElement | null>(null)
let observer: IntersectionObserver | null = null

// Same monotonic-token pattern as GameView so a fast slug nav can't let a
// stale response stomp state for the new slug.
let requestId = 0

async function loadTag() {
  const s = slug.value
  if (!s) return
  const token = requestId
  try {
    const result = await tags.getBySlug(s)
    if (token !== requestId) return
    tag.value = result
  } catch (err) {
    if (token !== requestId) return
    if (err instanceof ApiError && err.status === 404) {
      notFound.value = true
    } else {
      errored.value = true
    }
    throw err
  }
}

async function loadMore() {
  const s = slug.value
  if (!s || loading.value || reachedEnd.value || notFound.value) return
  const token = requestId
  loading.value = true
  paginationErrored.value = false
  try {
    const page = await tags.clips(s, { cursor: cursor.value })
    if (token !== requestId) return
    items.value.push(...page.items)
    cursor.value = page.nextCursor
    if (page.nextCursor === null) reachedEnd.value = true
  } catch (err) {
    if (token !== requestId) return
    if (items.value.length === 0) {
      if (err instanceof ApiError && err.status === 404) {
        notFound.value = true
      } else {
        errored.value = true
      }
    } else {
      paginationErrored.value = true
      detachObserver()
    }
    console.error('tag-detail: load failed', err)
  } finally {
    if (token === requestId) loading.value = false
  }
}

async function retryLoadMore() {
  await loadMore()
  if (!paginationErrored.value && !reachedEnd.value) {
    attachObserver()
  }
}

function attachObserver() {
  if (observer || !sentinel.value || reachedEnd.value) return
  observer = new IntersectionObserver(
    (entries) => {
      if (entries.some((e) => e.isIntersecting)) loadMore()
    },
    { rootMargin: '400px' },
  )
  observer.observe(sentinel.value)
}

function detachObserver() {
  observer?.disconnect()
  observer = null
}

async function loadAll() {
  const token = ++requestId
  errored.value = false
  notFound.value = false
  paginationErrored.value = false
  reachedEnd.value = false
  initialLoading.value = true
  tag.value = null
  items.value = []
  cursor.value = null
  loading.value = false
  detachObserver()

  try {
    await loadTag()
    if (token !== requestId) return
    if (notFound.value || errored.value) return
    await loadMore()
  } catch {
    // handlers set the right flag
  } finally {
    if (token === requestId) {
      initialLoading.value = false
      if (!notFound.value && !errored.value && !reachedEnd.value) {
        requestAnimationFrame(attachObserver)
      }
    }
  }
}

function retry() {
  loadAll()
}

onMounted(loadAll)
onBeforeUnmount(detachObserver)

watch(slug, () => {
  loadAll()
})
</script>

<template>
  <main class="mx-auto max-w-300 px-7 pt-7 pb-16 max-tablet:px-4">
    <StatusPanel v-if="notFound" kind="empty" message="No tag with that slug.">
      <RouterLink
        to="/"
        class="rounded-lg border border-border-strong px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
      >
        Back to feed
      </RouterLink>
    </StatusPanel>

    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load this tag.">
      <button
        class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="retry"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel v-else-if="initialLoading && !tag" kind="loading" message="Loading" />

    <template v-else-if="tag">
      <!-- The mint #tagname title is per spec — the tag itself is the accent. -->
      <PageHeader :title="`#${tag.slug}`" class="mb-7 [&_h1]:text-accent">
        <template #caption> {{ tag.clipCount }} clip{{ tag.clipCount === 1 ? '' : 's' }} </template>
      </PageHeader>

      <div
        v-if="items.length"
        class="grid grid-cols-4 gap-3.5 max-lg:grid-cols-2 max-tablet:grid-cols-1"
      >
        <ClipCard
          v-for="clip in items"
          :key="clip.id"
          :clip="clip"
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        />
      </div>

      <StatusPanel v-else-if="reachedEnd" kind="empty" message="No clips with this tag yet.">
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          class="rounded-lg border border-border-strong px-4 py-2 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        >
          Upload a clip
        </RouterLink>
      </StatusPanel>

      <div v-if="!reachedEnd" ref="sentinel" class="mt-8 py-6" aria-hidden="true"></div>
      <div
        v-if="loading && !reachedEnd"
        role="status"
        aria-live="polite"
        class="-mt-6 flex items-center justify-center py-3 text-[11px] text-text-muted"
      >
        Loading more…
      </div>

      <div v-if="paginationErrored" class="mt-2 flex flex-col items-center gap-2">
        <span class="text-[11px] text-text-muted">Couldn't load more — try again.</span>
        <button
          :disabled="loading"
          @click="retryLoadMore"
          class="cursor-pointer rounded-lg border border-border-strong bg-transparent px-6 py-2.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:opacity-50"
        >
          Retry
        </button>
      </div>
    </template>
  </main>
</template>
