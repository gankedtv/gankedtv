<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useThemeStore } from '@/stores/theme'
import { setPassword } from '@/api/auth'
import { ApiError } from '@/api/client'

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
  'h-11 rounded-sm border border-border bg-surface-raised px-3.5 font-body text-sm text-text-primary outline-none transition-colors duration-150 focus:border-ink'
</script>

<template>
  <div class="mx-auto flex w-full max-w-xl flex-col gap-9 px-6 pt-10 pb-30">
    <header>
      <p class="m-0 mb-2 font-mono text-[10px] uppercase tracking-[0.22em] text-text-secondary">
        <span class="text-ink">Account</span> · Your settings
      </p>
      <h1
        class="m-0 font-heading text-[clamp(32px,4vw,44px)] font-bold uppercase leading-none text-text-primary"
      >
        Settings
      </h1>
      <hr class="m-0 mt-5 h-px w-full border-0 bg-border" />
    </header>

    <!-- Section I — password. No card chrome: hairline-separated sections. -->
    <section>
      <p class="m-0 mb-1 font-mono text-[10px] uppercase tracking-[0.22em] text-text-secondary">
        <span class="text-ink">I</span> {{ isFirstTimeSet ? 'Set password' : 'Change password' }}
      </p>
      <p class="m-0 mb-4 font-body text-[13px] text-text-secondary">
        Adding a password lets you sign in with email + password in addition to your connected
        accounts.
      </p>

      <form class="flex flex-col gap-3" @submit="submit">
        <label class="flex flex-col gap-1.5">
          <span class="font-mono text-[10px] uppercase tracking-[0.18em] text-text-secondary">
            Current password
            <span v-if="isFirstTimeSet" class="normal-case tracking-normal text-text-muted">
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
          <span class="font-mono text-[10px] uppercase tracking-[0.18em] text-text-secondary">
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
          class="flex items-center justify-center gap-2 self-start bg-ink px-5 py-3 font-heading text-[14px] font-bold uppercase tracking-[0.06em] text-signal-text transition-[filter] duration-150 hover:brightness-108 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {{ submitting ? 'Saving…' : 'Save password' }}
        </button>
        <p
          v-if="formError"
          class="m-0 font-mono text-[11px] tracking-wide text-signal"
          role="alert"
        >
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
    </section>

    <hr class="m-0 h-px w-full border-0 bg-border" />

    <!-- Section II — appearance. -->
    <section>
      <p class="m-0 mb-1 font-mono text-[10px] uppercase tracking-[0.22em] text-text-secondary">
        <span class="text-ink">II</span> Appearance
      </p>
      <p class="m-0 mb-4 font-body text-[13px] text-text-secondary">
        One palette, two pressings. Pick the mode that suits the room.
      </p>
      <div class="flex gap-2" role="group" aria-label="Theme mode">
        <button
          type="button"
          :class="[
            'cursor-pointer border px-5 py-2.5 font-mono text-[11px] uppercase tracking-[0.12em] transition-colors duration-150',
            theme.isDark
              ? 'border-ink text-ink'
              : 'border-border text-text-secondary hover:border-ink hover:text-ink',
          ]"
          :aria-pressed="theme.isDark"
          @click="setMode(true)"
        >
          Dark
        </button>
        <button
          type="button"
          :class="[
            'cursor-pointer border px-5 py-2.5 font-mono text-[11px] uppercase tracking-[0.12em] transition-colors duration-150',
            !theme.isDark
              ? 'border-ink text-ink'
              : 'border-border text-text-secondary hover:border-ink hover:text-ink',
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
