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
  <main class="mx-auto max-w-360 px-8 pt-10 pb-30 max-tablet:px-4 max-tablet:pt-5 max-tablet:pb-20">
    <StatusPanel v-if="notFound" kind="empty" message="No tag with that slug.">
      <RouterLink
        to="/"
        class="border border-border px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
      >
        Back to feed
      </RouterLink>
    </StatusPanel>

    <StatusPanel v-else-if="errored" kind="error" message="Couldn't load this tag.">
      <button
        class="cursor-pointer border border-border bg-transparent px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
        @click="retry"
      >
        Retry
      </button>
    </StatusPanel>

    <StatusPanel v-else-if="initialLoading && !tag" kind="loading" message="Loading" />

    <template v-else-if="tag">
      <PageHeader :title="`#${tag.slug}`" class="mb-7">
        <template #caption>
          <span class="text-ink">Filed under</span>&nbsp;· {{ tag.clipCount }} clip{{
            tag.clipCount === 1 ? '' : 's'
          }}
        </template>
        <hr class="m-0 mt-5 h-px w-full border-0 bg-border" />
      </PageHeader>

      <div
        v-if="items.length"
        class="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-x-5.5 gap-y-7"
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
          class="border border-border px-4 py-2 font-mono text-xs uppercase tracking-widest text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink"
        >
          Upload a clip
        </RouterLink>
      </StatusPanel>

      <div v-if="!reachedEnd" ref="sentinel" class="mt-8 py-6" aria-hidden="true"></div>
      <div
        v-if="loading && !reachedEnd"
        role="status"
        aria-live="polite"
        class="-mt-6 flex items-center justify-center py-3 font-mono text-[11px] uppercase tracking-widest text-text-muted"
      >
        Loading more…
      </div>

      <div v-if="paginationErrored" class="mt-2 flex flex-col items-center gap-2">
        <span class="font-mono text-[11px] uppercase tracking-widest text-text-muted">
          Couldn't load more — try again.
        </span>
        <button
          :disabled="loading"
          @click="retryLoadMore"
          class="cursor-pointer border border-border bg-transparent px-6 py-2.5 font-mono text-[11px] uppercase tracking-[0.08em] text-text-primary transition-colors duration-150 hover:border-ink hover:text-ink disabled:opacity-50"
        >
          Retry
        </button>
      </div>
    </template>
  </main>
</template>
