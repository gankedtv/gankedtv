<script setup lang="ts">
import { ref, watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { register } from '@/api/auth'
import { ApiError } from '@/api/client'
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
const username = ref('')
const password = ref('')
const acceptedTerms = ref(false)
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
      acceptedTerms: acceptedTerms.value,
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
      const detail = errorDetail(err) ?? validationError(err)
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

// DataAnnotations failures come back as ValidationProblemDetails (`errors` dict keyed by
// field), not `detail` — surface the first message instead of the generic fallback.
function validationError(err: ApiError): string | null {
  const body = err.body as { errors?: Record<string, string[]> } | null
  const first = body?.errors ? Object.values(body.errors).flat()[0] : undefined
  return typeof first === 'string' && first.length > 0 ? first : null
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
          Create account
        </h1>
        <p class="m-0 text-[13px] text-text-secondary">
          Pick a handle, set a password. No third-party account required.
        </p>
      </div>

      <form class="flex flex-col gap-3" @submit="submitRegister">
        <label class="flex flex-col gap-1.5">
          <span class="text-[10px] font-bold uppercase tracking-widest text-text-secondary"
            >Email</span
          >
          <input
            v-model="email"
            type="email"
            autocomplete="email"
            required
            class="rounded-md border border-border bg-surface-high px-3 py-2.5 text-sm text-text-primary placeholder:text-text-muted transition-colors duration-150 focus:border-accent focus:outline-none"
          />
        </label>
        <label class="flex flex-col gap-1.5">
          <span class="text-[10px] font-bold uppercase tracking-widest text-text-secondary">
            Username
          </span>
          <input
            v-model="username"
            type="text"
            autocomplete="username"
            required
            minlength="1"
            maxlength="30"
            class="rounded-md border border-border bg-surface-high px-3 py-2.5 text-sm text-text-primary placeholder:text-text-muted transition-colors duration-150 focus:border-accent focus:outline-none"
          />
        </label>
        <label class="flex flex-col gap-1.5">
          <span class="text-[10px] font-bold uppercase tracking-widest text-text-secondary">
            Password (min 12)
          </span>
          <input
            v-model="password"
            type="password"
            autocomplete="new-password"
            required
            minlength="12"
            maxlength="128"
            class="rounded-md border border-border bg-surface-high px-3 py-2.5 text-sm text-text-primary placeholder:text-text-muted transition-colors duration-150 focus:border-accent focus:outline-none"
          />
        </label>
        <label class="flex items-start gap-2.5 text-xs text-text-secondary">
          <input
            v-model="acceptedTerms"
            type="checkbox"
            required
            class="mt-0.5 size-3.5 shrink-0 accent-accent"
          />
          <span>
            I agree to the
            <!-- New tab so ticking through the docs doesn't wipe the half-filled form. -->
            <RouterLink
              to="/terms"
              target="_blank"
              rel="noopener"
              class="font-semibold text-accent no-underline hover:underline"
              >Terms of Service</RouterLink
            >
            and
            <RouterLink
              to="/privacy"
              target="_blank"
              rel="noopener"
              class="font-semibold text-accent no-underline hover:underline"
              >Privacy Policy</RouterLink
            >.
          </span>
        </label>
        <button
          type="submit"
          :disabled="submitting"
          class="flex items-center justify-center gap-2 rounded-lg bg-accent px-5 py-3 text-sm font-bold text-[#080f0d] transition-[filter] duration-150 hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {{ submitting ? 'Creating account…' : 'Create account' }}
        </button>
        <p v-if="formError" class="m-0 text-xs font-medium text-accent" role="alert">
          {{ formError }}
        </p>
        <p class="m-0 mt-1 text-center text-xs text-text-secondary">
          Already have an account?
          <RouterLink
            :to="{ name: 'login', query: returnTo ? { redirect: returnTo } : {} }"
            class="font-semibold text-accent no-underline hover:underline"
            >Sign in</RouterLink
          >
        </p>
      </form>
    </div>
  </div>
</template>
