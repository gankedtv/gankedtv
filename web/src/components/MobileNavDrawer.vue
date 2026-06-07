<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, useId, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useThemeStore } from '@/stores/theme'
import ThemePicker from './ThemePicker.vue'
import IconSun from './icons/IconSun.vue'
import IconMoon from './icons/IconMoon.vue'

// The drawer is the mobile home for everything the desktop bar exposes inline: the nav links
// plus profile/admin and the theme controls (which collapse off the top bar on mobile).
const auth = useAuthStore()
const theme = useThemeStore()

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ 'update:open': [boolean] }>()

const titleId = useId()
const firstLinkRef = ref<HTMLAnchorElement | null>(null)

function close() {
  emit('update:open', false)
}

// Focus the first nav item on open so keyboard users land inside the drawer instead of
// staying on the (now-offscreen) hamburger trigger. Re-focus on every open transition.
watch(
  () => props.open,
  (open) => {
    if (open) nextTick(() => firstLinkRef.value?.focus())
  },
)

// Close on route change so navigating from the drawer doesn't leave it hanging open
// over the destination page.
const route = useRoute()
watch(
  () => route.fullPath,
  () => {
    if (props.open) close()
  },
)

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open) close()
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

// Active-link styling mirrors AppNav's desktop nav so the drawer reads as the same
// navigation surface — the brand-light underline cue is the only "you are here" signal
// users have learned, so don't reinvent it here.
const linkBase =
  'relative block px-5 py-3 font-heading text-base font-medium uppercase tracking-[0.04em] text-text-secondary no-underline transition-colors duration-150 hover:bg-surface-overlay hover:text-text-primary'
const linkActive =
  "text-text-primary after:content-[''] after:absolute after:left-5 after:right-5 after:bottom-1.5 after:h-0.5 after:bg-brand-light"
</script>

<template>
  <Teleport to="body">
    <Transition
      enter-active-class="transition-opacity duration-200"
      enter-from-class="opacity-0"
      leave-active-class="transition-opacity duration-150"
      leave-to-class="opacity-0"
    >
      <div
        v-if="open"
        class="fixed inset-0 z-[60]"
        role="dialog"
        aria-modal="true"
        :aria-labelledby="titleId"
      >
        <!-- Backdrop -->
        <div class="absolute inset-0 bg-black/70" @click="close" />

        <!-- Drawer panel: slides in from the left so it doesn't fight the bell/avatar
             dropdowns that anchor to the right edge. -->
        <Transition
          enter-active-class="transition-transform duration-200 ease-out"
          enter-from-class="-translate-x-full"
          leave-active-class="transition-transform duration-150 ease-in"
          leave-to-class="-translate-x-full"
        >
          <aside
            v-if="open"
            class="absolute inset-y-0 left-0 z-10 flex w-72 max-w-[80vw] flex-col border-r border-border bg-surface-raised shadow-[0_0_40px_var(--color-brand-glow)]"
            @click.stop
          >
            <div class="flex items-center justify-between border-b border-border px-5 py-4">
              <h2
                :id="titleId"
                class="font-heading text-base font-bold uppercase tracking-[0.04em] text-text-primary"
              >
                Menu
              </h2>
              <button
                type="button"
                aria-label="Close menu"
                class="cursor-pointer font-mono text-xl leading-none text-text-muted transition-colors duration-150 hover:text-text-primary"
                @click="close"
              >
                ×
              </button>
            </div>

            <nav class="flex flex-col py-2" aria-label="Mobile main navigation">
              <RouterLink
                ref="firstLinkRef"
                to="/"
                :class="linkBase"
                :exact-active-class="linkActive"
              >
                Feed
              </RouterLink>
              <RouterLink to="/games" :class="linkBase" :active-class="linkActive">
                Games
              </RouterLink>
              <RouterLink to="/trending" :class="linkBase" :active-class="linkActive">
                Trending
              </RouterLink>
              <RouterLink to="/leaderboards" :class="linkBase" :active-class="linkActive">
                Leaderboards
              </RouterLink>
              <RouterLink
                v-if="auth.isAuthenticated && auth.user"
                :to="`/user/${auth.user.username}`"
                :class="linkBase"
                :active-class="linkActive"
              >
                Profile
              </RouterLink>
              <RouterLink
                v-if="auth.isModerator"
                to="/admin"
                :class="linkBase"
                :active-class="linkActive"
              >
                Admin
              </RouterLink>
            </nav>

            <!-- Theme controls + sign-in pinned to the bottom: they collapse off the mobile
                 top bar, so the drawer is the only place to reach them on small screens. -->
            <div class="mt-auto flex flex-col gap-3 border-t border-border px-5 py-4">
              <div class="flex items-center justify-between gap-3">
                <span class="font-mono text-[10px] uppercase tracking-widest text-text-muted">
                  Theme
                </span>
                <div class="flex items-center gap-2">
                  <ThemePicker />
                  <button
                    type="button"
                    class="inline-flex h-9 w-9 cursor-pointer items-center justify-center rounded-md border border-border bg-transparent text-text-secondary transition-colors duration-150 hover:border-border-hover hover:text-text-primary"
                    :title="theme.isDark ? 'Switch to light' : 'Switch to dark'"
                    :aria-label="theme.isDark ? 'Switch to light mode' : 'Switch to dark mode'"
                    :aria-pressed="theme.isDark"
                    @click="theme.toggle()"
                  >
                    <IconSun v-if="theme.isDark" :size="16" />
                    <IconMoon v-else :size="16" />
                  </button>
                </div>
              </div>
              <RouterLink
                v-if="!auth.isAuthenticated"
                to="/login"
                class="inline-flex h-9 items-center justify-center rounded-md bg-brand px-4 text-[13px] font-semibold uppercase tracking-[0.02em] text-white no-underline transition-colors duration-150 hover:bg-brand-light"
              >
                Sign In
              </RouterLink>
            </div>
          </aside>
        </Transition>
      </div>
    </Transition>
  </Teleport>
</template>
