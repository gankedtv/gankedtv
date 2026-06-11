<script setup lang="ts">
import { ref, watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { register } from '@/api/auth'
import { ApiError } from '@/api/client'

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
const username = ref('')
const password = ref('')
const submitting = ref(false)
const formError = ref<string | null>(null)

async function submitRegister(event: Event) {
  event.preventDefault()
  formError.value = null
  submitting.value = true
  try {
    const tokens = await register({
      email: email.value,
      username: username.value,
      password: password.value,
    })
    auth.setSession(tokens.token, tokens.refresh)
    await auth.fetchMe()
    router.replace(returnTo || '/')
  } catch (err) {
    formError.value = mapRegisterError(err)
  } finally {
    submitting.value = false
  }
}

function mapRegisterError(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 409) {
      return 'An account with this email already exists. Sign in with your existing method, then attach a password from your profile.'
    }
    if (err.status === 429) {
      return 'Too many registration attempts. Wait a minute and try again.'
    }
    if (err.status === 400) {
      const detail = errorDetail(err)
      if (detail) return detail
      return 'Check the email and password requirements (min 12 characters) and try again.'
    }
  }
  return 'Sign-up failed. Try again.'
}

function errorDetail(err: ApiError): string | null {
  const body = err.body as { detail?: string } | null
  return body && typeof body.detail === 'string' && body.detail.length > 0 ? body.detail : null
}
</script>

<template>
  <div class="flex min-h-[calc(100vh-4rem)] flex-col items-center justify-center gap-8 px-6">
    <div class="flex flex-col items-center gap-2.5 text-center">
      <div class="flex items-center gap-2.5">
        <span class="size-2 bg-ink" aria-hidden="true"></span>
        <span
          class="font-display text-[28px] font-bold uppercase tracking-[0.04em] text-text-primary"
        >
          GANKED<span class="text-ink">.TV</span>
        </span>
      </div>
      <div class="font-mono text-[11px] uppercase tracking-widest text-text-muted">
        No algorithm. Just clips.
      </div>
    </div>

    <div class="w-full max-w-100 border border-border bg-surface-base px-8 py-9">
      <div class="mb-6 text-center">
        <p class="m-0 mb-2 font-mono text-[10px] uppercase tracking-[0.22em] text-ink">
          Join the archive
        </p>
        <h1
          class="m-0 mb-2 font-heading text-[32px] font-bold uppercase leading-none text-text-primary"
        >
          Start Filing
        </h1>
        <p class="m-0 font-body text-[13px] text-text-secondary">
          Pick a handle, set a password — no third-party account required.
        </p>
      </div>

      <form class="flex flex-col gap-3" @submit="submitRegister">
        <label class="flex flex-col gap-1.5">
          <span class="font-mono text-[10px] uppercase tracking-[0.18em] text-text-secondary"
            >Email</span
          >
          <input
            v-model="email"
            type="email"
            autocomplete="email"
            required
            class="h-11 rounded-sm border border-border bg-surface-raised px-3.5 font-body text-sm text-text-primary outline-none transition-colors duration-150 focus:border-ink"
          />
        </label>
        <label class="flex flex-col gap-1.5">
          <span class="font-mono text-[10px] uppercase tracking-[0.18em] text-text-secondary">
            Username
          </span>
          <input
            v-model="username"
            type="text"
            autocomplete="username"
            required
            minlength="1"
            maxlength="30"
            class="h-11 rounded-sm border border-border bg-surface-raised px-3.5 font-body text-sm text-text-primary outline-none transition-colors duration-150 focus:border-ink"
          />
        </label>
        <label class="flex flex-col gap-1.5">
          <span class="font-mono text-[10px] uppercase tracking-[0.18em] text-text-secondary">
            Password (min 12)
          </span>
          <input
            v-model="password"
            type="password"
            autocomplete="new-password"
            required
            minlength="12"
            maxlength="128"
            class="h-11 rounded-sm border border-border bg-surface-raised px-3.5 font-body text-sm text-text-primary outline-none transition-colors duration-150 focus:border-ink"
          />
        </label>
        <button
          type="submit"
          :disabled="submitting"
          class="flex items-center justify-center gap-2 bg-ink px-5 py-3 font-heading text-[15px] font-bold uppercase tracking-[0.06em] text-signal-text transition-[filter] duration-150 hover:brightness-108 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {{ submitting ? 'Creating account…' : 'Create account' }}
        </button>
        <p
          v-if="formError"
          class="m-0 font-mono text-[11px] tracking-wide text-signal"
          role="alert"
        >
          {{ formError }}
        </p>
        <p class="m-0 mt-1 text-center font-body text-xs text-text-secondary">
          Already have an account?
          <RouterLink
            :to="{ name: 'login', query: returnTo ? { redirect: returnTo } : {} }"
            class="font-heading uppercase tracking-[0.04em] text-ink no-underline hover:underline"
            >Sign in</RouterLink
          >
        </p>
      </form>
    </div>
  </div>
</template>
