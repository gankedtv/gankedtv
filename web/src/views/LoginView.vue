<script setup lang="ts">
import { ref, watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { login, oauthStartUrl } from '@/api/auth'
import { api, ApiError } from '@/api/client'
import IconDiscord from '@/components/icons/IconDiscord.vue'
import IconGoogle from '@/components/icons/IconGoogle.vue'

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

async function devSignIn(username = 'seeduser') {
  devLoading.value = true
  devError.value = null
  try {
    const res = await api<{ token: string; refresh: string }>('/dev/token', {
      method: 'POST',
      body: { username },
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
  <div class="flex min-h-[calc(100vh-4rem)] flex-col items-center justify-center gap-8 px-6">
    <!-- Logo + tagline -->
    <div class="flex flex-col items-center gap-2.5 text-center">
      <div class="flex items-center gap-2.5">
        <span class="logo__mark"></span>
        <span
          class="font-display text-[28px] font-bold uppercase tracking-[0.04em] text-text-primary"
        >
          GANKED.TV
        </span>
      </div>
      <div class="font-mono text-[11px] uppercase tracking-widest text-text-muted">
        No algorithm. Just clips.
      </div>
    </div>

    <!-- Card -->
    <div class="w-full max-w-100 rounded-lg border border-border bg-surface-raised px-8 py-9">
      <div class="mb-6 text-center">
        <h1 class="m-0 mb-2 font-heading text-[32px] font-bold uppercase text-text-primary">
          Sign In
        </h1>
        <p class="m-0 font-body text-sm text-text-secondary">
          Sign in with your email or a connected account
        </p>
      </div>

      <!-- Email/password form -->
      <form class="flex flex-col gap-3" @submit="submitLogin">
        <label class="flex flex-col gap-1.5">
          <span class="font-mono text-[10px] uppercase tracking-widest text-text-muted">Email</span>
          <input
            v-model="email"
            type="email"
            autocomplete="email"
            required
            class="rounded-md border border-border-strong bg-surface-overlay px-3 py-2 font-body text-sm text-text-primary outline-none focus:border-border-hover"
          />
        </label>
        <label class="flex flex-col gap-1.5">
          <span class="font-mono text-[10px] uppercase tracking-widest text-text-muted">
            Password
          </span>
          <input
            v-model="password"
            type="password"
            autocomplete="current-password"
            required
            class="rounded-md border border-border-strong bg-surface-overlay px-3 py-2 font-body text-sm text-text-primary outline-none focus:border-border-hover"
          />
        </label>
        <button
          type="submit"
          :disabled="submitting"
          class="flex items-center justify-center gap-2 rounded-md bg-brand px-5 py-3 font-heading text-[15px] font-bold uppercase tracking-[0.06em] text-white transition-colors duration-150 hover:bg-brand-light disabled:cursor-not-allowed disabled:opacity-50"
        >
          {{ submitting ? 'Signing in…' : 'Sign in' }}
        </button>
        <p v-if="formError" class="m-0 font-mono text-[11px] tracking-wide text-error" role="alert">
          {{ formError }}
        </p>
        <p class="m-0 mt-1 text-center font-body text-xs text-text-secondary">
          New to GankedTV?
          <RouterLink
            :to="{ name: 'register', query: returnTo ? { redirect: returnTo } : {} }"
            class="font-heading uppercase tracking-[0.04em] text-text-primary no-underline hover:text-brand"
            >Create an account</RouterLink
          >
        </p>
      </form>

      <div class="my-4 flex items-center gap-3">
        <div class="h-px flex-1 bg-border"></div>
        <span class="font-mono text-[10px] uppercase tracking-widest text-text-muted"> or </span>
        <div class="h-px flex-1 bg-border"></div>
      </div>

      <div class="flex flex-col gap-3">
        <!-- Discord button -->
        <a
          :href="oauthStartUrl('discord', returnTo)"
          class="flex items-center justify-center gap-2.5 rounded-md bg-discord px-5 py-3 font-heading text-[15px] font-bold uppercase tracking-[0.06em] text-white no-underline transition-colors duration-150 hover:bg-discord-hover"
        >
          <IconDiscord :size="20" class="shrink-0" />
          Continue with Discord
        </a>

        <!-- Google button -->
        <a
          :href="oauthStartUrl('google', returnTo)"
          class="flex items-center justify-center gap-2.5 rounded-md border border-border-strong bg-surface-overlay px-5 py-3 font-heading text-[15px] font-bold uppercase tracking-[0.06em] text-text-primary no-underline transition-[background-color,border-color] duration-150 hover:border-border-hover hover:bg-surface-raised"
        >
          <IconGoogle :size="20" class="shrink-0" />
          Continue with Google
        </a>
      </div>

      <!-- Dev sign-in (local only — never bundled in production builds) -->
      <div v-if="isDev" class="mt-5 border-t border-border pt-5">
        <p class="m-0 mb-2 font-mono text-[10px] uppercase tracking-widest text-text-muted">
          Dev mode
        </p>
        <button
          type="button"
          :disabled="devLoading"
          class="flex w-full items-center justify-center gap-2 rounded-md border border-border-strong bg-surface-overlay px-5 py-2.5 font-heading text-[13px] font-bold uppercase tracking-[0.06em] text-text-primary transition-[background-color,border-color] duration-150 hover:border-border-hover hover:bg-surface-raised disabled:cursor-not-allowed disabled:opacity-50"
          @click="devSignIn()"
        >
          {{ devLoading ? 'Signing in…' : 'Sign in as seeduser' }}
        </button>
        <p v-if="devError" class="m-0 mt-2 text-center font-mono text-[10px] text-error">
          {{ devError }}
        </p>
      </div>

      <!-- Footer -->
      <p class="m-0 mt-5 text-center font-mono text-[10px] tracking-wider text-text-muted">
        By signing in you agree to our Terms of Service.
      </p>
    </div>
  </div>
</template>
