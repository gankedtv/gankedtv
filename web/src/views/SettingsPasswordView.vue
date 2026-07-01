<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useThemeStore } from '@/stores/theme'
import { setPassword } from '@/api/auth'
import { ApiError } from '@/api/client'
import PageHeader from '@/components/PageHeader.vue'

const router = useRouter()
const auth = useAuthStore()
const theme = useThemeStore()

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
    // Sync the local user state so the heading flips from "Set password" to
    // "Change password" without waiting for the next /auth/me poll. The server
    // just confirmed the write (204), so we know the new state is correct —
    // skip the extra roundtrip that fetchMe() would cost.
    if (auth.user) {
      auth.user.hasPassword = true
    }
    successMessage.value = 'Password updated.'
    currentPassword.value = ''
    newPassword.value = ''
  } catch (err) {
    if (err instanceof ApiError && err.status === 401) {
      // Token rejected — bounce back to login. Do this in the catch (not in the
      // pure mapError helper) so navigation/logout side effects stay co-located
      // with the error-handling flow, matching the LoginView/RegisterView pattern.
      auth.logout()
      router.replace({ name: 'login' })
    }
    formError.value = mapError(err)
  } finally {
    submitting.value = false
  }
}

function mapError(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 401) {
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

function setMode(dark: boolean) {
  if (theme.isDark !== dark) theme.toggle()
}

const inputClass =
  'rounded-md border border-border bg-surface-high px-3 py-2.5 text-sm text-text-primary placeholder:text-text-muted transition-colors duration-150 focus:border-accent focus:outline-none'
</script>

<template>
  <div class="mx-auto w-full max-w-xl px-7 pt-7 pb-16 max-tablet:px-4">
    <PageHeader title="Settings">
      <template #caption> <span class="text-accent">Account</span> · Your settings </template>
    </PageHeader>

    <!-- Password section. No card chrome: border-separated sections. -->
    <section class="mt-7">
      <p class="m-0 mb-1 text-[10px] font-bold uppercase tracking-[0.14em] text-accent">
        {{ isFirstTimeSet ? 'Set password' : 'Change password' }}
      </p>
      <p class="m-0 mb-4 text-[13px] text-text-secondary">
        Adding a password lets you sign in with email + password in addition to your connected
        accounts.
      </p>

      <form class="flex flex-col gap-3" @submit="submit">
        <label class="flex flex-col gap-1.5">
          <span class="text-[10px] font-bold uppercase tracking-widest text-text-secondary">
            Current password
            <span
              v-if="isFirstTimeSet"
              class="font-normal normal-case tracking-normal text-text-muted"
            >
              (leave blank if you don't have one yet)
            </span>
          </span>
          <input
            v-model="currentPassword"
            type="password"
            autocomplete="current-password"
            :class="inputClass"
          />
        </label>
        <label class="flex flex-col gap-1.5">
          <span class="text-[10px] font-bold uppercase tracking-widest text-text-secondary">
            New password (min 12)
          </span>
          <input
            v-model="newPassword"
            type="password"
            autocomplete="new-password"
            required
            minlength="12"
            maxlength="128"
            :class="inputClass"
          />
        </label>
        <button
          type="submit"
          :disabled="submitting"
          class="flex items-center justify-center gap-2 self-start rounded-lg bg-accent px-5 py-2.5 text-sm font-bold text-[#080f0d] transition-[filter] duration-150 hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {{ submitting ? 'Saving…' : 'Save password' }}
        </button>
        <p v-if="formError" class="m-0 text-xs font-medium text-accent" role="alert">
          {{ formError }}
        </p>
        <p v-if="successMessage" class="m-0 text-xs font-medium text-text-secondary" role="status">
          {{ successMessage }}
        </p>
      </form>
    </section>

    <!-- Appearance section. -->
    <section class="mt-8 border-t border-border pt-7">
      <p class="m-0 mb-1 text-[10px] font-bold uppercase tracking-[0.14em] text-accent">
        Appearance
      </p>
      <p class="m-0 mb-4 text-[13px] text-text-secondary">Pick the mode that suits the room.</p>
      <div class="flex gap-2" role="group" aria-label="Theme mode">
        <button
          type="button"
          :class="[
            'cursor-pointer rounded-lg border px-5 py-2 text-xs font-semibold transition-colors duration-150',
            theme.isDark
              ? 'border-accent-border bg-accent-bg text-accent'
              : 'border-border text-text-secondary hover:border-accent hover:text-accent',
          ]"
          :aria-pressed="theme.isDark"
          @click="setMode(true)"
        >
          Dark
        </button>
        <button
          type="button"
          :class="[
            'cursor-pointer rounded-lg border px-5 py-2 text-xs font-semibold transition-colors duration-150',
            !theme.isDark
              ? 'border-accent-border bg-accent-bg text-accent'
              : 'border-border text-text-secondary hover:border-accent hover:text-accent',
          ]"
          :aria-pressed="!theme.isDark"
          @click="setMode(false)"
        >
          Light
        </button>
      </div>
    </section>
  </div>
</template>
