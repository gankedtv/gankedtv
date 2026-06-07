<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useTemplateRef, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useThemeStore } from '@/stores/theme'
import { useNotificationsStore } from '@/stores/notifications'
import { search, type SearchResponse } from '@/api/search'
import ThemePicker from './ThemePicker.vue'
import UserAvatar from './UserAvatar.vue'
import GameSearchResult from './GameSearchResult.vue'
import NotificationsDropdown from './notifications/NotificationsDropdown.vue'
import IconSearch from './icons/IconSearch.vue'
import IconSun from './icons/IconSun.vue'
import IconMoon from './icons/IconMoon.vue'
import IconPlus from './icons/IconPlus.vue'
import IconBell from './icons/IconBell.vue'
import IconMenu from './icons/IconMenu.vue'
import MobileNavDrawer from './MobileNavDrawer.vue'

const auth = useAuthStore()
const theme = useThemeStore()
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

const navLinkActive =
  "text-text-primary after:content-[''] after:absolute after:left-3.5 after:right-3.5 after:bottom-0.5 after:h-0.5 after:bg-brand-light"

// --- Search box state ---------------------------------------------------------
//
// Decorative input until now (issue #86). Wires to GET /search via api/search.ts.
// Layout: combobox-pattern input with a popover listbox below, top 5 clips + top
// 3 games. Enter navigates to the full /search results view.
//
// Tokens used: kept the existing `border-border bg-surface-overlay` shell from
// the prior decorative div so the visual mass of the navbar doesn't shift.

const SEARCH_DEBOUNCE_MS = 250
const DROPDOWN_CLIP_LIMIT = 5
const DROPDOWN_GAME_LIMIT = 3

const query = ref('')
const isFocused = ref(false)
const results = ref<SearchResponse>({ clips: [], games: [] })
const loading = ref(false)

// The dropdown is teleported to <body> so it doesn't get clipped/stack-trapped by
// the header's `backdrop-filter` paint context — which was rendering page content
// over the dropdown's bottom rows and breaking hit-testing for clicks. Living in
// the root stacking context means it just needs a high z-index to beat the header.
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

const bellPopoverStyle = computed(() => ({
  top: `${bellPopoverPos.value.top}px`,
  right: `${bellPopoverPos.value.right}px`,
  width: '360px',
}))

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

// --- Mobile nav drawer --------------------------------------------------------
//
// Below the `tablet` breakpoint (720px), Games/Trending/Leaderboards are hidden via
// max-tablet:hidden on the desktop nav links. The hamburger + drawer surface them on
// mobile so two top-level features aren't unreachable. Drawer owns its own escape /
// route-change / backdrop close handling; we just track open-state here and return
// focus to the hamburger when it closes.
const hamburgerRef = useTemplateRef<HTMLButtonElement>('hamburgerRef')
const isDrawerOpen = ref(false)

watch(isDrawerOpen, (open) => {
  if (!open) nextTick(() => hamburgerRef.value?.focus())
})

// --- Mobile search overlay ----------------------------------------------------
//
// The inline search bar only renders ≥1281px (see template). On narrower screens a
// search icon opens this full-width overlay over the nav bar, reusing the same query
// + onSubmit so there's a single search implementation. Submit/Esc close it; we focus
// the input on open and return focus to the trigger on close.
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
  <header
    class="sticky top-0 z-50 h-16 border-b border-border bg-[color-mix(in_oklab,var(--color-surface-base)_85%,transparent)] backdrop-blur-[14px]"
  >
    <div class="mx-auto flex h-full max-w-360 min-w-0 items-center gap-5 px-6 *:shrink-0">
      <!-- Logo — wordmark collapses to the mark alone on mobile to free top-bar space. -->
      <RouterLink
        to="/"
        aria-label="GankedTV home"
        class="flex items-center gap-2 font-display text-[22px] font-bold uppercase tracking-[0.06em] text-text-primary no-underline"
      >
        <span class="logo__mark"></span>
        <span class="max-tablet:hidden">GANKED<span class="logo__tv">.TV</span></span>
      </RouterLink>

      <!-- Hamburger trigger (mobile only). Below the tablet breakpoint, Games / Trending /
           Leaderboards collapse into the drawer below. -->
      <button
        ref="hamburgerRef"
        type="button"
        class="hidden h-9 w-9 cursor-pointer items-center justify-center rounded-md border border-border bg-transparent text-text-secondary transition-colors duration-150 hover:border-border-hover hover:text-text-primary max-tablet:inline-flex"
        aria-label="Open menu"
        :aria-expanded="isDrawerOpen"
        @click="isDrawerOpen = true"
      >
        <IconMenu :size="16" />
      </button>

      <!-- Nav links (desktop). The whole row collapses into the drawer below the tablet
           breakpoint so the mobile bar shows only the mark, hamburger, and actions. -->
      <nav class="flex flex-1 items-center gap-1 max-tablet:hidden" aria-label="Main navigation">
        <RouterLink
          to="/"
          class="relative rounded-sm px-3.5 py-2 text-[13px] font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary"
          :exact-active-class="navLinkActive"
        >
          Feed
        </RouterLink>
        <RouterLink
          to="/games"
          class="relative rounded-sm px-3.5 py-2 text-[13px] font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary"
          :active-class="navLinkActive"
        >
          Games
        </RouterLink>
        <RouterLink
          to="/trending"
          class="relative rounded-sm px-3.5 py-2 text-[13px] font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary"
          :active-class="navLinkActive"
        >
          Trending
        </RouterLink>
        <RouterLink
          to="/leaderboards"
          class="relative rounded-sm px-3.5 py-2 text-[13px] font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary"
          :active-class="navLinkActive"
        >
          Leaderboards
        </RouterLink>
      </nav>

      <!-- Search (desktop only) -->
      <div class="hidden min-w-0 shrink min-[1281px]:block">
        <div
          ref="inputWrapperRef"
          class="flex h-9 w-60 max-w-60 items-center gap-2 overflow-hidden rounded-md border bg-surface-overlay px-3 font-mono text-xs whitespace-nowrap transition-colors duration-150"
          :class="isFocused ? 'border-brand text-text-primary' : 'border-border text-text-muted'"
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
            class="min-w-0 flex-1 border-0 bg-transparent font-mono text-xs text-text-primary placeholder:text-text-muted focus:outline-none"
            @focus="isFocused = true"
            @blur="onBlur"
            @keydown.enter.prevent="onSubmit"
            @keydown.escape="($event.target as HTMLInputElement).blur()"
          />
        </div>

        <!-- Dropdown — teleported to body so the header's backdrop-filter context
             can't trap or clip its rendering. Position is computed from the input
             wrapper's bounding rect (see updatePopoverPos). Panel bg uses
             surface-raised so the row-hover surface-overlay reads as a brighter band. -->
        <Teleport to="body">
          <div
            v-if="showPopover"
            id="nav-search-results"
            :style="popoverStyle"
            class="fixed z-[60] overflow-hidden rounded-md border border-border-strong bg-surface-raised shadow-[0_18px_50px_-18px_rgba(0,0,0,0.6)]"
            @mousedown.prevent
          >
            <div
              v-if="loading && results.clips.length === 0 && results.games.length === 0"
              class="px-3.5 py-3 font-mono text-[11px] uppercase tracking-widest text-text-muted"
            >
              Searching…
            </div>
            <template v-else>
              <div v-if="results.games.length > 0">
                <div
                  class="px-3.5 pt-2.5 pb-1 font-mono text-[10px] uppercase tracking-widest text-text-muted"
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
                  class="px-3.5 pt-2.5 pb-1 font-mono text-[10px] uppercase tracking-widest text-text-muted"
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
                    class="flex cursor-pointer items-center gap-3 px-3.5 py-2 transition-colors duration-150 hover:bg-surface-overlay"
                    @mousedown.prevent="onResultClick({ name: 'clip', params: { id: c.id } })"
                  >
                    <img
                      :src="c.thumbnailUrl"
                      alt=""
                      class="h-9 w-16 shrink-0 rounded-sm object-cover"
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

      <!-- Actions -->
      <div class="ml-auto flex items-center gap-2">
        <!-- Mobile search trigger — the inline search bar is ≥1281px only, so narrower
             screens get an icon that opens the full-width overlay below. -->
        <button
          ref="mobileSearchTriggerRef"
          type="button"
          class="inline-flex h-9 w-9 cursor-pointer items-center justify-center rounded-md border border-border bg-transparent text-text-secondary transition-colors duration-150 hover:border-border-hover hover:text-text-primary min-[1281px]:hidden"
          aria-label="Search"
          @click="openMobileSearch"
        >
          <IconSearch :size="16" />
        </button>

        <!-- Theme controls — collapse into the drawer below the tablet breakpoint. -->
        <div class="flex items-center gap-2 max-tablet:hidden">
          <!-- Theme picker (Underground / Tactical / Arcade) -->
          <ThemePicker />

          <!-- Light/dark toggle -->
          <button
            class="inline-flex h-9 w-9 cursor-pointer items-center justify-center rounded-md border border-border bg-transparent text-text-secondary transition-all duration-150 hover:border-border-hover hover:text-text-primary"
            :title="theme.isDark ? 'Switch to light' : 'Switch to dark'"
            :aria-label="theme.isDark ? 'Switch to light mode' : 'Switch to dark mode'"
            :aria-pressed="!theme.isDark"
            @click="theme.toggle()"
          >
            <IconSun v-if="theme.isDark" :size="16" />
            <IconMoon v-else :size="16" />
          </button>
        </div>

        <!-- Notifications bell (authenticated only) -->
        <button
          v-if="auth.isAuthenticated"
          ref="bellRef"
          type="button"
          class="relative inline-flex h-9 w-9 cursor-pointer items-center justify-center rounded-md border bg-transparent text-text-secondary transition-all duration-150 hover:border-border-hover hover:text-text-primary"
          :class="isBellOpen ? 'border-brand text-text-primary' : 'border-border'"
          :aria-label="`Notifications${unreadBadge ? ` (${unreadBadge} unread)` : ''}`"
          :aria-expanded="isBellOpen"
          @click="toggleBell"
        >
          <IconBell :size="16" />
          <span
            v-if="unreadBadge"
            class="absolute -top-1 -right-1 inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-error px-1 font-mono text-[10px] leading-none font-semibold text-white"
          >
            {{ unreadBadge }}
          </span>
        </button>

        <!-- Admin link (moderator / admin only) — in the drawer on mobile -->
        <RouterLink
          v-if="auth.isModerator"
          to="/admin"
          class="inline-flex h-9 cursor-pointer items-center rounded-md border border-border bg-transparent px-3 font-heading text-[12px] font-bold uppercase tracking-wider text-text-secondary no-underline transition-colors duration-150 hover:border-border-hover hover:text-text-primary max-tablet:hidden"
        >
          Admin
        </RouterLink>

        <!-- Upload button -->
        <RouterLink
          v-if="auth.isAuthenticated"
          to="/upload"
          class="inline-flex h-9 cursor-pointer items-center rounded-md bg-brand px-4 text-[13px] font-semibold uppercase tracking-[0.02em] text-white no-underline transition-colors duration-150 hover:bg-brand-light"
        >
          <span class="inline-flex items-center gap-1.5">
            <IconPlus :size="12" :stroke-width="2.5" />
            <span class="hidden min-[1041px]:inline">Upload</span>
          </span>
        </RouterLink>

        <!-- Sign in -->
        <RouterLink
          v-else
          to="/login"
          class="inline-flex h-9 cursor-pointer items-center rounded-md bg-brand px-4 text-[13px] font-semibold uppercase tracking-[0.02em] text-white no-underline transition-colors duration-150 hover:bg-brand-light"
        >
          Sign In
        </RouterLink>

        <!-- Avatar -->
        <RouterLink
          v-if="auth.isAuthenticated && auth.user"
          :to="`/user/${auth.user.username}`"
          class="inline-flex"
        >
          <UserAvatar :user="auth.user" :size="36" />
        </RouterLink>
      </div>
    </div>

    <!-- Mobile search overlay — fills the bar when the search icon is tapped (inline search
         is ≥1281px only). Reuses the same query + onSubmit, so there's one search path;
         Enter opens the /search results view. -->
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

    <!-- Bell popover — teleported to body so the header's backdrop-filter context can't trap or
         clip it (same reasoning as the search dropdown). Positioned by updateBellPos(). -->
    <Teleport to="body">
      <div
        v-if="isBellOpen"
        id="nav-notifications-popover"
        :style="bellPopoverStyle"
        class="fixed z-[60]"
      >
        <NotificationsDropdown @close="closeBell" />
      </div>
    </Teleport>

    <MobileNavDrawer v-model:open="isDrawerOpen" />
  </header>
</template>
