<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useTemplateRef, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useNotificationsStore } from '@/stores/notifications'
import { search, type SearchResponse } from '@/api/search'
import { volIssMeta } from '@/lib/issue'
import ThemeModeToggle from './ThemeModeToggle.vue'
import UserAvatar from './UserAvatar.vue'
import GameSearchResult from './GameSearchResult.vue'
import NotificationsDropdown from './notifications/NotificationsDropdown.vue'
import IconSearch from './icons/IconSearch.vue'
import IconPlus from './icons/IconPlus.vue'
import IconBell from './icons/IconBell.vue'

const auth = useAuthStore()
const router = useRouter()
const notificationsStore = useNotificationsStore()

// Start/stop polling whenever the auth state flips. Wiring it here (vs App.vue) keeps the
// nav self-contained — every authenticated session is rendered through the nav, so the bell
// component owns the lifetime.
watch(
  () => auth.isAuthenticated,
  (isAuthed) => {
    if (isAuthed) {
      notificationsStore.startPolling()
    } else {
      notificationsStore.reset()
    }
  },
  { immediate: true },
)
onBeforeUnmount(() => notificationsStore.stopPolling())

// 2px ink underline that overlaps the nav's bottom rule — the printed
// active-tab gesture (padding-bottom 6px / margin-bottom -8px per spec).
const navLinkActive = 'text-text-primary border-ink'

// --- Search box state ---------------------------------------------------------
//
// Decorative input until now (issue #86). Wires to GET /search via api/search.ts.
// Layout: combobox-pattern input with a popover listbox below, top 5 clips + top
// 3 games. Enter navigates to the full /search results view.

const SEARCH_DEBOUNCE_MS = 250
const DROPDOWN_CLIP_LIMIT = 5
const DROPDOWN_GAME_LIMIT = 3

const query = ref('')
const isFocused = ref(false)
const results = ref<SearchResponse>({ clips: [], games: [] })
const loading = ref(false)

// The dropdown is teleported to <body> so it escapes the header's stacking
// context and never gets clipped by it. Living in the root stacking context
// means it just needs a high z-index to beat the header.
const inputWrapperRef = useTemplateRef<HTMLDivElement>('inputWrapperRef')
// Coordinates the teleported popover anchors to. Recomputed on focus + on resize
// while the popover is visible — scroll isn't tracked because the header is sticky,
// so the input's viewport position is stable.
const popoverPos = ref({ top: 0, left: 0, width: 0 })

function updatePopoverPos() {
  const el = inputWrapperRef.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  popoverPos.value = {
    top: rect.bottom + 4, // matches the previous mt-1 (4px) gap
    left: rect.left,
    width: rect.width,
  }
}

const popoverStyle = computed(() => ({
  top: `${popoverPos.value.top}px`,
  left: `${popoverPos.value.left}px`,
  width: `${popoverPos.value.width}px`,
}))

const showPopover = computed(() => isFocused.value && query.value.trim().length > 0)

watch(showPopover, async (open) => {
  if (!open) return
  await nextTick()
  updatePopoverPos()
})

function onResize() {
  if (showPopover.value) updatePopoverPos()
}

onMounted(() => window.addEventListener('resize', onResize))
onBeforeUnmount(() => window.removeEventListener('resize', onResize))

let debounceTimer: ReturnType<typeof setTimeout> | undefined
// requestSeq ensures a slow earlier response can't overwrite a fast later one.
let requestSeq = 0

async function runSearch(raw: string) {
  const trimmed = raw.trim()
  if (!trimmed) {
    results.value = { clips: [], games: [] }
    loading.value = false
    return
  }
  const seq = ++requestSeq
  loading.value = true
  try {
    // Backend caps games to whatever the user gets via `limit`; we ask for the
    // larger of the two visual caps and slice in the template. One request,
    // simplest contract.
    const resp = await search.query(trimmed, {
      type: 'all',
      limit: Math.max(DROPDOWN_CLIP_LIMIT, DROPDOWN_GAME_LIMIT),
    })
    if (seq !== requestSeq) return
    results.value = resp
  } catch {
    if (seq !== requestSeq) return
    // Silent failure in the dropdown is intentional: typing-on-every-keystroke
    // would otherwise turn a transient network blip into a flashing toast.
    results.value = { clips: [], games: [] }
  } finally {
    if (seq === requestSeq) loading.value = false
  }
}

watch(query, (q) => {
  // cancelInFlight bumps requestSeq so any pending fetch resolves into the stale
  // branch — without this, the next keystroke would only clear the debounce timer
  // and an in-flight response could land between debounces, briefly painting stale
  // results for the previous query.
  cancelInFlight()
  if (!q.trim()) {
    results.value = { clips: [], games: [] }
    return
  }
  debounceTimer = setTimeout(() => runSearch(q), SEARCH_DEBOUNCE_MS)
})

onBeforeUnmount(() => clearTimeout(debounceTimer))

// Bumps requestSeq so any in-flight fetch resolves into a stale-branch and
// can't overwrite `results` after we navigate. Also clears the debounce timer
// so a queued search doesn't fire after the user has already moved on.
function cancelInFlight() {
  clearTimeout(debounceTimer)
  requestSeq++
  loading.value = false
}

function onSubmit() {
  const trimmed = query.value.trim()
  if (!trimmed) return
  cancelInFlight()
  isFocused.value = false
  isMobileSearchOpen.value = false
  void router.push({ name: 'search', query: { q: trimmed } })
}

function onResultClick(to: { name: string; params: Record<string, string> }) {
  // Clear focus so the dropdown closes; navigate after the focus state settles.
  cancelInFlight()
  isFocused.value = false
  query.value = ''
  void router.push(to)
}

// Slight blur delay so a click on a result still fires before the dropdown unmounts.
function onBlur() {
  setTimeout(() => {
    isFocused.value = false
  }, 120)
}

// --- Notifications bell + dropdown --------------------------------------------

const bellRef = useTemplateRef<HTMLButtonElement>('bellRef')
const isBellOpen = ref(false)
const bellPopoverPos = ref({ top: 0, right: 0 })

function updateBellPos() {
  const el = bellRef.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  bellPopoverPos.value = {
    top: rect.bottom + 4,
    // Anchor to the right edge of the bell so the dropdown sits flush with the nav's right side.
    right: Math.max(8, window.innerWidth - rect.right),
  }
}

const bellPopoverStyle = computed(() => {
  const right = bellPopoverPos.value.right
  return {
    top: `${bellPopoverPos.value.top}px`,
    right: `${right}px`,
    // Cap to the space between the right anchor and an 8px left gutter so the 360px panel
    // never runs off a narrow viewport (it otherwise overflows the left edge on mobile).
    width: `min(360px, calc(100vw - ${right}px - 8px))`,
  }
})

async function toggleBell() {
  isBellOpen.value = !isBellOpen.value
  if (isBellOpen.value) {
    await nextTick()
    updateBellPos()
  }
}

function closeBell() {
  isBellOpen.value = false
}

const unreadBadge = computed(() => {
  const n = notificationsStore.unreadCount
  if (n <= 0) return null
  return n > 9 ? '9+' : String(n)
})

// Close-on-outside-click. Listening on `mousedown` (not click) so the bell can swallow its own
// open-click without immediately reclosing.
function onDocumentMouseDown(e: MouseEvent) {
  if (!isBellOpen.value) return
  const target = e.target as Node | null
  if (target && bellRef.value && bellRef.value.contains(target)) return
  const popover = document.getElementById('nav-notifications-popover')
  if (popover && target && popover.contains(target)) return
  isBellOpen.value = false
}

// Esc closes the popover from anywhere. Listening at the document level (not on the popover)
// because focus may sit on the row link, the "see all" button, or the bell itself — a scoped
// handler would miss most of those cases. Returning focus to the bell preserves keyboard flow.
function onDocumentKeyDown(e: KeyboardEvent) {
  if (!isBellOpen.value || e.key !== 'Escape') return
  isBellOpen.value = false
  bellRef.value?.focus()
}

onMounted(() => {
  document.addEventListener('mousedown', onDocumentMouseDown)
  document.addEventListener('keydown', onDocumentKeyDown)
})
onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocumentMouseDown)
  document.removeEventListener('keydown', onDocumentKeyDown)
})

// --- Mobile search overlay ----------------------------------------------------
// Below 1281px the inline search bar is hidden; a search icon opens this overlay, reusing
// the same query + onSubmit. Focus the input on open, return focus to the trigger on close.
const isMobileSearchOpen = ref(false)
const mobileSearchRef = useTemplateRef<HTMLInputElement>('mobileSearchRef')
const mobileSearchTriggerRef = useTemplateRef<HTMLButtonElement>('mobileSearchTriggerRef')

function openMobileSearch() {
  isMobileSearchOpen.value = true
  nextTick(() => mobileSearchRef.value?.focus())
}

function closeMobileSearch() {
  isMobileSearchOpen.value = false
  nextTick(() => mobileSearchTriggerRef.value?.focus())
}
</script>

<template>
  <header class="sticky top-0 z-50 h-16 border-b border-border bg-surface-base">
    <div class="mx-auto flex h-full max-w-360 min-w-0 items-center gap-6 px-6 *:shrink-0 max-tablet:px-4">
      <!-- Logo — wordmark collapses to the mark alone on the smallest screens. -->
      <RouterLink
        to="/"
        aria-label="GankedTV home"
        class="flex items-center gap-2.5 no-underline"
      >
        <span class="size-2 bg-ink" aria-hidden="true"></span>
        <span
          class="font-display text-[17px] font-bold uppercase tracking-[0.04em] text-text-primary max-[420px]:hidden"
        >
          GANKED<span class="text-ink">.TV</span>
        </span>
      </RouterLink>

      <!-- Desktop nav links — the bottom tab bar takes over below 1024px. -->
      <nav class="flex flex-1 items-center gap-6 max-lg:hidden" aria-label="Main navigation">
        <RouterLink
          to="/"
          class="-mb-2 border-b-2 border-transparent pb-1.5 font-mono text-[11px] uppercase tracking-[0.12em] text-text-secondary no-underline transition-colors duration-150 hover:text-ink"
          :exact-active-class="navLinkActive"
        >
          Feed
        </RouterLink>
        <RouterLink
          to="/games"
          class="-mb-2 border-b-2 border-transparent pb-1.5 font-mono text-[11px] uppercase tracking-[0.12em] text-text-secondary no-underline transition-colors duration-150 hover:text-ink"
          :active-class="navLinkActive"
        >
          Games
        </RouterLink>
        <RouterLink
          to="/trending"
          class="-mb-2 border-b-2 border-transparent pb-1.5 font-mono text-[11px] uppercase tracking-[0.12em] text-text-secondary no-underline transition-colors duration-150 hover:text-ink"
          :active-class="navLinkActive"
        >
          Trending
        </RouterLink>
        <RouterLink
          to="/leaderboards"
          class="-mb-2 border-b-2 border-transparent pb-1.5 font-mono text-[11px] uppercase tracking-[0.12em] text-text-secondary no-underline transition-colors duration-150 hover:text-ink"
          :active-class="navLinkActive"
        >
          Leaderboards
        </RouterLink>
      </nav>

      <!-- Search (desktop only) -->
      <div class="hidden min-w-0 shrink min-[1281px]:block">
        <div
          ref="inputWrapperRef"
          class="flex h-8.5 w-60 max-w-60 items-center gap-2 overflow-hidden border bg-surface-raised px-3 font-mono text-[11px] whitespace-nowrap transition-colors duration-150"
          :class="isFocused ? 'border-ink text-text-primary' : 'border-border text-text-muted'"
        >
          <IconSearch :size="14" :stroke-width="2.2" class="shrink-0" />
          <input
            v-model="query"
            type="search"
            role="combobox"
            aria-controls="nav-search-results"
            aria-autocomplete="list"
            :aria-expanded="isFocused && query.trim().length > 0"
            placeholder="search clips, games"
            class="min-w-0 flex-1 border-0 bg-transparent font-mono text-[11px] text-text-primary placeholder:text-text-muted focus:outline-none"
            @focus="isFocused = true"
            @blur="onBlur"
            @keydown.enter.prevent="onSubmit"
            @keydown.escape="($event.target as HTMLInputElement).blur()"
          />
        </div>

        <!-- Dropdown — teleported to body so the header's stacking context can't
             trap or clip its rendering. Position is computed from the input
             wrapper's bounding rect (see updatePopoverPos). -->
        <Teleport to="body">
          <div
            v-if="showPopover"
            id="nav-search-results"
            :style="popoverStyle"
            class="fixed z-60 overflow-hidden border border-border-strong bg-surface-base"
            @mousedown.prevent
          >
            <div
              v-if="loading && results.clips.length === 0 && results.games.length === 0"
              class="flex items-center gap-3 px-3.5 py-3"
            >
              <span class="block h-1.5 w-5.5 overflow-hidden bg-surface-raised">
                <span class="block h-full w-full origin-left bg-ink animate-[tick_1.6s_ease-in-out_infinite]"></span>
              </span>
              <span class="font-mono text-[11px] uppercase tracking-widest text-text-muted">Searching</span>
            </div>
            <template v-else>
              <div v-if="results.games.length > 0">
                <div
                  class="px-3.5 pt-2.5 pb-1 font-mono text-[10px] uppercase tracking-[0.18em] text-text-muted"
                >
                  Games
                </div>
                <ul role="listbox" class="m-0 list-none p-0">
                  <GameSearchResult
                    v-for="g in results.games.slice(0, DROPDOWN_GAME_LIMIT)"
                    :key="g.id"
                    :tag="g.tag"
                    :name="g.name"
                    @select="onResultClick({ name: 'game-detail', params: { slug: g.slug } })"
                  />
                </ul>
              </div>
              <div v-if="results.clips.length > 0">
                <div
                  class="px-3.5 pt-2.5 pb-1 font-mono text-[10px] uppercase tracking-[0.18em] text-text-muted"
                  :class="{ 'border-t border-border': results.games.length > 0 }"
                >
                  Clips
                </div>
                <ul role="listbox" class="m-0 list-none p-0">
                  <li
                    v-for="c in results.clips.slice(0, DROPDOWN_CLIP_LIMIT)"
                    :key="c.id"
                    role="option"
                    :aria-selected="false"
                    class="flex cursor-pointer items-center gap-3 px-3.5 py-2 transition-colors duration-150 hover:bg-surface-raised"
                    @mousedown.prevent="onResultClick({ name: 'clip', params: { id: c.id } })"
                  >
                    <img
                      :src="c.thumbnailUrl"
                      alt=""
                      class="h-9 w-16 shrink-0 border border-border object-cover"
                    />
                    <span class="min-w-0 flex-1 truncate font-body text-sm text-text-primary">
                      {{ c.title }}
                    </span>
                  </li>
                </ul>
              </div>
              <div
                v-if="!loading && results.clips.length === 0 && results.games.length === 0"
                class="px-3.5 py-3 font-mono text-[11px] uppercase tracking-widest text-text-muted"
              >
                No matches
              </div>
            </template>
          </div>
        </Teleport>
      </div>

      <!-- Vol/Iss meta strip — the page is an issue, not a feed. -->
      <span
        class="font-mono text-[10px] uppercase tracking-[0.15em] whitespace-nowrap text-text-muted max-[1100px]:hidden"
      >
        {{ volIssMeta() }}
      </span>

      <!-- Actions -->
      <div class="ml-auto flex items-center gap-2">
        <!-- Mobile search trigger (inline bar is ≥1281px only). -->
        <button
          ref="mobileSearchTriggerRef"
          type="button"
          class="inline-flex size-8.5 cursor-pointer items-center justify-center border border-border bg-transparent text-text-secondary transition-colors duration-150 hover:border-ink hover:text-ink min-[1281px]:hidden"
          aria-label="Search"
          @click="openMobileSearch"
        >
          <IconSearch :size="16" />
        </button>

        <ThemeModeToggle />

        <!-- Notifications bell (authenticated only) -->
        <button
          v-if="auth.isAuthenticated"
          ref="bellRef"
          type="button"
          class="relative inline-flex size-8.5 cursor-pointer items-center justify-center border bg-transparent text-text-secondary transition-colors duration-150 hover:border-ink hover:text-ink"
          :class="isBellOpen ? 'border-ink text-ink' : 'border-border'"
          :aria-label="`Notifications${unreadBadge ? ` (${unreadBadge} unread)` : ''}`"
          :aria-expanded="isBellOpen"
          @click="toggleBell"
        >
          <IconBell :size="16" />
          <span
            v-if="unreadBadge"
            class="absolute -top-1 -right-1 inline-flex h-4 min-w-4 items-center justify-center bg-ink px-1 font-mono text-[10px] leading-none font-semibold text-signal-text"
          >
            {{ unreadBadge }}
          </span>
        </button>

        <!-- Admin link (moderator / admin only) — lives in the footer on mobile -->
        <RouterLink
          v-if="auth.isModerator"
          to="/admin"
          class="inline-flex h-8.5 cursor-pointer items-center border border-border bg-transparent px-3 font-mono text-[11px] uppercase tracking-[0.12em] text-text-secondary no-underline transition-colors duration-150 hover:border-ink hover:text-ink max-lg:hidden"
        >
          Admin
        </RouterLink>

        <!-- Upload button — the tab bar carries upload below 1024px -->
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          class="inline-flex h-8.5 cursor-pointer items-center bg-ink px-4 text-[13px] font-medium uppercase tracking-[0.02em] text-signal-text no-underline transition-[filter] duration-150 hover:brightness-108 max-lg:hidden"
        >
          <span class="inline-flex items-center gap-1.5">
            <IconPlus :size="12" :stroke-width="2.5" />
            <span>Upload</span>
          </span>
        </RouterLink>

        <!-- Sign in -->
        <RouterLink
          v-else
          to="/login"
          class="inline-flex h-8.5 cursor-pointer items-center bg-ink px-4 text-[13px] font-medium uppercase tracking-[0.02em] text-signal-text no-underline transition-[filter] duration-150 hover:brightness-108 max-lg:hidden"
        >
          Sign In
        </RouterLink>

        <!-- Avatar — the tab bar carries the profile destination below 1024px -->
        <RouterLink
          v-if="auth.isAuthenticated && auth.user"
          :to="`/user/${auth.user.username}`"
          class="inline-flex max-lg:hidden"
        >
          <UserAvatar :user="auth.user" :size="34" />
        </RouterLink>
      </div>
    </div>

    <!-- Mobile search overlay — fills the bar when the trigger is tapped; Enter opens /search. -->
    <div
      v-if="isMobileSearchOpen"
      class="absolute inset-0 z-20 flex items-center gap-2 bg-surface-base px-6 min-[1281px]:hidden"
    >
      <IconSearch :size="16" class="shrink-0 text-text-muted" />
      <input
        ref="mobileSearchRef"
        v-model="query"
        type="search"
        aria-label="Search clips and games"
        placeholder="search clips, games"
        class="min-w-0 flex-1 border-0 bg-transparent font-mono text-sm text-text-primary placeholder:text-text-muted focus:outline-none"
        @keydown.enter.prevent="onSubmit"
        @keydown.escape="closeMobileSearch"
      />
      <button
        type="button"
        aria-label="Close search"
        class="shrink-0 cursor-pointer px-2 font-mono text-xl leading-none text-text-muted transition-colors duration-150 hover:text-text-primary"
        @click="closeMobileSearch"
      >
        ×
      </button>
    </div>

    <!-- Bell popover — teleported to body so the header's stacking context can't trap or
         clip it (same reasoning as the search dropdown). Positioned by updateBellPos(). -->
    <Teleport to="body">
      <div
        v-if="isBellOpen"
        id="nav-notifications-popover"
        :style="bellPopoverStyle"
        class="fixed z-60"
      >
        <NotificationsDropdown @close="closeBell" />
      </div>
    </Teleport>
  </header>
</template>
