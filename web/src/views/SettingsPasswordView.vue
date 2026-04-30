<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { setPassword } from '@/api/auth'
import { ApiError } from '@/api/client'

const router = useRouter()
const auth = useAuthStore()

// /auth/me reports `hasPassword` so we can switch between "Set password" (OAuth-only,
// first-time attach) and "Change password" (rotation) copy. While the user is still
// loading we default to the rotation form — harmless: if no password is set on the
// server, the currentPassword field is just left blank and the server accepts it.
const isFirstTimeSet = computed(() => auth.user?.hasPassword === false)

const currentPassword = ref('')
const newPassword = ref('')
const submitting = ref(false)
const formError = ref<string | null>(null)
const successMessage = ref<string | null>(null)

async function submit(event: Event) {
  event.preventDefault()
  formError.value = null
  successMessage.value = null
  submitting.value = true
  try {
    const current = currentPassword.value.length > 0 ? currentPassword.value : null
    await setPassword(current, newPassword.value)
    successMessage.value = 'Password updated.'
    currentPassword.value = ''
    newPassword.value = ''
  } catch (err) {
    formError.value = mapError(err)
  } finally {
    submitting.value = false
  }
}

function mapError(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 401) {
      // Token rejected — bounce back to login.
      auth.logout()
      router.replace({ name: 'login' })
      return 'Session expired. Sign in again.'
    }
    if (err.status === 400) {
      const detail = (err.body as { code?: string; detail?: string } | null)?.detail
      const code = (err.body as { code?: string } | null)?.code
      if (code === 'wrong_current_password') {
        return 'The current password you entered is incorrect.'
      }
      if (typeof detail === 'string' && detail.length > 0) {
        return detail
      }
      return 'Password rejected. Pick a longer one (min 12 characters).'
    }
  }
  return 'Update failed. Try again.'
}
</script>

<template>
  <div class="mx-auto flex w-full max-w-xl flex-col gap-6 px-6 py-10">
    <header>
      <h1 class="m-0 mb-1 font-heading text-[28px] font-bold uppercase text-text-primary">
        {{ isFirstTimeSet ? 'Set password' : 'Change password' }}
      </h1>
      <p class="m-0 font-body text-sm text-text-secondary">
        Adding a password lets you sign in with email + password in addition to your connected
        accounts.
      </p>
    </header>

    <form
      class="flex flex-col gap-3 rounded-lg border border-border bg-surface-raised px-6 py-6"
      @submit="submit"
    >
      <label class="flex flex-col gap-1.5">
        <span class="font-mono text-[10px] uppercase tracking-widest text-text-muted">
          Current password
          <span v-if="isFirstTimeSet" class="normal-case tracking-normal text-text-muted">
            (leave blank if you don't have one yet)
          </span>
        </span>
        <input
          v-model="currentPassword"
          type="password"
          autocomplete="current-password"
          class="rounded-md border border-border-strong bg-surface-overlay px-3 py-2 font-body text-sm text-text-primary outline-none focus:border-border-hover"
        />
      </label>
      <label class="flex flex-col gap-1.5">
        <span class="font-mono text-[10px] uppercase tracking-widest text-text-muted">
          New password (min 12)
        </span>
        <input
          v-model="newPassword"
          type="password"
          autocomplete="new-password"
          required
          minlength="12"
          maxlength="128"
          class="rounded-md border border-border-strong bg-surface-overlay px-3 py-2 font-body text-sm text-text-primary outline-none focus:border-border-hover"
        />
      </label>
      <button
        type="submit"
        :disabled="submitting"
        class="flex items-center justify-center gap-2 rounded-md bg-brand px-5 py-3 font-heading text-[14px] font-bold uppercase tracking-[0.06em] text-white transition-colors duration-150 hover:bg-brand-light disabled:cursor-not-allowed disabled:opacity-50"
      >
        {{ submitting ? 'Saving…' : 'Save password' }}
      </button>
      <p v-if="formError" class="m-0 font-mono text-[11px] tracking-wide text-error" role="alert">
        {{ formError }}
      </p>
      <p
        v-if="successMessage"
        class="m-0 font-mono text-[11px] tracking-wide text-text-secondary"
        role="status"
      >
        {{ successMessage }}
      </p>
    </form>
  </div>
</template>
