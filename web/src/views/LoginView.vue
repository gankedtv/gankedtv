<script setup lang="ts">
import { nextTick, ref, watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { login, oauthStartUrl } from '@/api/auth'
import { api, ApiError } from '@/api/client'
import IconDiscord from '@/components/icons/IconDiscord.vue'
import IconGoogle from '@/components/icons/IconGoogle.vue'
import LogoMark from '@/components/LogoMark.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const rawRedirect = route.query.redirect
let returnTo: string | undefined
if (
  typeof rawRedirect === 'string' &&
  rawRedirect.startsWith('/') &&
  !rawRedirect.startsWith('//')
) {
  returnTo = rawRedirect
}

watchEffect(() => {
  if (auth.isAuthenticated) {
    router.replace(returnTo || '/')
  }
})

const email = ref('')
const password = ref('')
const submitting = ref(false)
const formError = ref<string | null>(null)

// Progressive disclosure: keep the OAuth buttons as the primary path on first
// paint and only reveal the email form when the user explicitly opts into it.
// Matches the convention used by GitHub / Linear / Vercel etc — fewest fields
// to reach the most-common path.
const showEmailForm = ref(false)
const emailInputRef = ref<HTMLInputElement | null>(null)

async function revealEmailForm() {
  showEmailForm.value = true
  // Auto-focus the email input on the next tick so keyboard users don't lose
  // momentum tabbing through the OAuth buttons before reaching it.
  await nextTick()
  emailInputRef.value?.focus()
}

async function submitLogin(event: Event) {
  event.preventDefault()
  formError.value = null
  submitting.value = true
  try {
    const tokens = await login({ email: email.value, password: password.value })
    auth.setSession(tokens.token, tokens.refresh)
    await auth.fetchMe()
    router.replace(returnTo || '/')
  } catch (err) {
    formError.value = mapLoginError(err)
  } finally {
    submitting.value = false
  }
}

function mapLoginError(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 401) {
      return 'Email or password is incorrect. If you signed up with Discord or Google, use those buttons instead.'
    }
    if (err.status === 429) {
      return 'Too many sign-in attempts. Wait a minute and try again.'
    }
    if (err.status === 400) {
      return 'Please enter a valid email address.'
    }
  }
  return 'Sign-in failed. Try again.'
}

// Dev-only sign-in: hits the /dev/token endpoint (mounted only when the API is in
// Development mode) and drops the resulting JWT into the auth store. Kept alongside
// the email/password form because it bypasses password auth — useful for tests where
// the seeded password might have been rotated.
const isDev = import.meta.env.DEV
const devLoading = ref(false)
const devError = ref<string | null>(null)

async function devSignIn(username = 'seeduser', role?: 'user' | 'moderator' | 'admin') {
  devLoading.value = true
  devError.value = null
  try {
    const res = await api<{ token: string; refresh: string }>('/dev/token', {
      method: 'POST',
      // `role` is honoured server-side only in Development; the API ignores unknown values
      // and falls back to "user". Omitting it preserves the existing seeduser behavior.
      body: role ? { username, role } : { username },
    })
    auth.setSession(res.token, res.refresh)
    await auth.fetchMe()
    router.replace(returnTo || '/')
  } catch (err) {
    devError.value =
      err instanceof Error
        ? err.message
        : 'Dev sign-in failed. Is the API running in Development mode?'
  } finally {
    devLoading.value = false
  }
}
</script>

<template>
  <div class="flex min-h-[calc(100vh-4rem)] flex-col items-center justify-center px-6">
    <!-- Card — just the card on the page surface, no hero or texture. -->
    <div class="mx-auto w-full max-w-sm rounded-lg border border-border bg-surface-raised p-8">
      <!-- Logo + tagline -->
      <div class="mb-6 flex flex-col items-center gap-2.5 text-center">
        <div class="flex items-center gap-2.5">
          <LogoMark :size="28" />
          <span
            class="font-condensed text-[26px] font-black uppercase leading-none tracking-[0.04em] text-text-primary"
          >
            GANKED<span class="text-accent">.TV</span>
          </span>
        </div>
        <div class="text-[11px] text-text-muted">No algorithm. Just clips.</div>
      </div>

      <div class="mb-6 text-center">
        <h1
          class="m-0 mb-2 font-condensed text-2xl font-black uppercase leading-none text-text-primary"
        >
          Sign in
        </h1>
        <p class="m-0 text-[13px] text-text-secondary">
          Sign in with your email or a connected account
        </p>
      </div>

      <!-- OAuth buttons (top — modern convention: surface the fastest path first) -->
      <div class="flex flex-col gap-3">
        <a
          :href="oauthStartUrl('discord', returnTo)"
          class="flex items-center justify-center gap-2.5 rounded-lg bg-discord px-5 py-3 text-sm font-semibold text-white no-underline transition-colors duration-150 hover:bg-discord-hover"
        >
          <IconDiscord :size="20" class="shrink-0" />
          Continue with Discord
        </a>

        <!-- Google's dark button spec (identity-locked colors; the border keeps it
             visible against the raised card). -->
        <a
          :href="oauthStartUrl('google', returnTo)"
          class="flex items-center justify-center gap-2.5 rounded-lg border border-border-strong bg-google px-5 py-3 text-sm font-semibold text-white no-underline transition-colors duration-150 hover:bg-google-hover"
        >
          <IconGoogle :size="20" class="shrink-0" />
          Continue with Google
        </a>
      </div>

      <div class="my-4 flex items-center gap-3">
        <div class="h-px flex-1 bg-border"></div>
        <span class="text-[10px] font-bold uppercase tracking-[0.14em] text-text-muted"> or </span>
        <div class="h-px flex-1 bg-border"></div>
      </div>

      <!-- Collapsed state: a single "Continue with email" button matching the
           OAuth buttons' visual weight. Clicking expands to the full form. -->
      <button
        v-if="!showEmailForm"
        type="button"
        class="flex w-full items-center justify-center gap-2.5 rounded-lg border border-border-strong px-5 py-3 text-sm font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent"
        @click="revealEmailForm"
      >
        Continue with email
      </button>

      <!-- Expanded state: actual email/password form -->
      <form v-else class="flex flex-col gap-3" @submit="submitLogin">
        <label class="flex flex-col gap-1.5">
          <span class="text-[10px] font-bold uppercase tracking-widest text-text-secondary"
            >Email</span
          >
          <input
            ref="emailInputRef"
            v-model="email"
            type="email"
            autocomplete="email"
            required
            class="rounded-md border border-border bg-surface-high px-3 py-2.5 text-sm text-text-primary placeholder:text-text-muted transition-colors duration-150 focus:border-accent focus:outline-none"
          />
        </label>
        <label class="flex flex-col gap-1.5">
          <span class="text-[10px] font-bold uppercase tracking-widest text-text-secondary">
            Password
          </span>
          <input
            v-model="password"
            type="password"
            autocomplete="current-password"
            required
            class="rounded-md border border-border bg-surface-high px-3 py-2.5 text-sm text-text-primary placeholder:text-text-muted transition-colors duration-150 focus:border-accent focus:outline-none"
          />
        </label>
        <button
          type="submit"
          :disabled="submitting"
          class="flex items-center justify-center gap-2 rounded-lg bg-accent px-5 py-3 text-sm font-bold text-[#080f0d] transition-[filter] duration-150 hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {{ submitting ? 'Signing in…' : 'Sign in with email' }}
        </button>
        <p v-if="formError" class="m-0 text-xs font-medium text-accent" role="alert">
          {{ formError }}
        </p>
      </form>

      <!-- Always-visible registration link — new users won't think to expand
           the email form first, so it stays outside the conditional. -->
      <p class="m-0 mt-4 text-center text-xs text-text-secondary">
        New to GankedTV?
        <RouterLink
          :to="{ name: 'register', query: returnTo ? { redirect: returnTo } : {} }"
          class="font-semibold text-accent no-underline hover:underline"
          >Create an account</RouterLink
        >
      </p>

      <!-- Dev sign-in (local only — never bundled in production builds) -->
      <div v-if="isDev" class="mt-5 border-t border-border pt-5">
        <p class="m-0 mb-2 text-[10px] font-bold uppercase tracking-[0.14em] text-text-muted">
          Dev mode
        </p>
        <button
          type="button"
          :disabled="devLoading"
          class="flex w-full items-center justify-center gap-2 rounded-lg border border-border-strong px-5 py-2.5 text-xs font-semibold text-text-secondary transition-colors duration-150 hover:border-accent hover:text-accent disabled:cursor-not-allowed disabled:opacity-50"
          @click="devSignIn()"
        >
          {{ devLoading ? 'Signing in…' : 'Sign in as seeduser' }}
        </button>
        <button
          type="button"
          :disabled="devLoading"
          class="mt-2 flex w-full items-center justify-center gap-2 rounded-lg border border-accent-border px-5 py-2.5 text-xs font-semibold text-accent transition-colors duration-150 hover:bg-accent-bg disabled:cursor-not-allowed disabled:opacity-50"
          @click="devSignIn('seedadmin', 'admin')"
        >
          {{ devLoading ? 'Signing in…' : 'Sign in as seedadmin' }}
        </button>
        <p v-if="devError" class="m-0 mt-2 text-center text-xs font-medium text-accent">
          {{ devError }}
        </p>
      </div>

      <!-- Footer -->
      <p class="m-0 mt-5 text-center text-[10px] text-text-muted">
        By signing in you agree to our Terms of Service.
      </p>
    </div>
  </div>
</template>
